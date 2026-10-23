using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Pages.Weather.Components;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class OpenMeteoWeatherCardTests
{
    private static readonly WeatherTimeSlice Day = new()
    {
        Time = MalaysiaTime.At(MalaysiaTime.Today.AddDays(1)),
        TemperatureC = 30,
        Summary = "Clear sky"
    };

    [Fact]
    public async Task OutlookShowsDailyTripDetailsAndRequestsThem()
    {
        var day = Day with
        {
            MaxTemperatureC = 30.1,
            MinTemperatureC = 24.2,
            ProbabilityOfRain = 0.93,
            PrecipitationMm = 7.4,
            Wind = "8.5 m/s",
            UvIndex = 9.2,
            Sunrise = MalaysiaTime.At(Day.Time.Date.AddHours(7).AddMinutes(4)),
            Sunset = MalaysiaTime.At(Day.Time.Date.AddHours(19).AddMinutes(11))
        };

        var options = LoadOpenMeteoOptions();
        Uri? outlookUri = null;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var realPlanner = new WeatherPlannerService(
            new HttpClient(new CapturingHandler(uri => outlookUri = uri)),
            cache,
            Options.Create(new CacheExpirationOptions
            {
                Weather = new() { LiveSeconds = 600, OutlookSeconds = 10800, FallbackSeconds = 30 }
            }),
            Options.Create(new WeatherOptions { OpenMeteo = options }));
        await realPlanner.RefreshProviderAsync(
            "Open-Meteo",
            new WeatherQuery { Latitude = 3.139, Longitude = 101.6869 },
            isOutlook: true);

        Assert.NotNull(outlookUri);
        var url = outlookUri.AbsoluteUri;
        Assert.Contains("precipitation_sum", url);
        Assert.Contains("temperature_2m_min", url);
        Assert.Contains("wind_speed_10m_max", url);
        Assert.Contains("uv_index_max", url);
        Assert.Contains("sunrise,sunset", url);

        await WithCardAsync(async (card, renderer, html, planner, _) =>
        {
            var markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("30.1", markup);
            Assert.Contains("Low 24.2", markup);
            Assert.Contains("93%", markup);
            Assert.Contains("7.4 mm", markup);
            Assert.Contains("Wind max", markup);
            Assert.Contains("8.5 m/s", markup);
            Assert.Contains("UV max", markup);
            Assert.Contains("9.2", markup);
            Assert.Contains("forecast-wind", markup);
            Assert.Contains("forecast-uv", markup);

            SetupHourly(planner, new WeatherProviderResult { Periods = [day] });
            await ClickAsync(card, renderer, "ShowHourlyForecastAsync", day);
            markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("Sunrise 07:04", markup);
            Assert.Contains("Sunset 19:11", markup);
        }, day);
    }

    [Fact]
    public async Task SingleDailyRecordOpensFullDayRequestAndShowsOnlySelectedMalaysiaDate()
    {
        var options = LoadOpenMeteoOptions();
        Uri? hourlyUri = null;
        const string rawHourlyJson = """
            {
              "hourly": {
                "time": [
                  "2026-10-14T23:00:00+08:00",
                  "2026-10-15T00:00:00+08:00",
                  "2026-10-14T00:00:00+08:00",
                  "2026-10-13T23:00:00+08:00"
                ],
                "temperature_2m": [27.0, 26.0, 25.0, 28.0],
                "precipitation_probability": [20, 10, 30, 15],
                "precipitation": [0.0, 0.0, 4.2, 0.0],
                "wind_speed_10m": [12.0, 5.0, 6.0, 7.0],
                "weather_code": [1, 0, 2, 0]
              }
            }
            """;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var realPlanner = new WeatherPlannerService(
            new HttpClient(new CapturingHandler(uri => hourlyUri = uri, rawHourlyJson)),
            cache,
            Options.Create(new CacheExpirationOptions
            {
                Weather = new() { LiveSeconds = 600, OutlookSeconds = 10800, FallbackSeconds = 30 }
            }),
            Options.Create(new WeatherOptions { OpenMeteo = options }));
        var targetDate = new DateTime(2026, 10, 14);
        var hourlyResult = await realPlanner.GetHourlyForecastAsync(
            new WeatherQuery { Latitude = 3.139, Longitude = 101.6869 },
            targetDate);

        Assert.NotNull(hourlyUri);
        var url = Uri.UnescapeDataString(hourlyUri.AbsoluteUri);
        Assert.Contains($"start_date={targetDate:yyyy-MM-dd}&end_date={targetDate:yyyy-MM-dd}", url);
        Assert.Contains("latitude=3.1390&longitude=101.6869", url);
        Assert.Contains($"timezone={MalaysiaTime.IanaTimeZone}", url);
        Assert.DoesNotContain("forecast_hours", url);

        await WithCardAsync(async (card, renderer, html, planner, js) =>
        {
            Assert.Contains("aria-haspopup=\"dialog\"", await renderer.Dispatcher.InvokeAsync(html.ToHtmlString));
            SetupHourly(planner, new WeatherProviderResult
            {
                Periods =
                [
                    Day with { Time = Day.Time.ToUniversalTime(), Summary = "First hour", PrecipitationMm = 4.2 },
                    Day with { Time = Day.Time.AddHours(23), Summary = "Last hour", Wind = "12 km/h" }
                ]
            });

            await ClickAsync(card, renderer, "ShowHourlyForecastAsync", Day);

            js.Verify(runtime => runtime.InvokeAsync<It.IsAnyType>("dialogHelper.showModal", It.IsAny<object[]>()), Times.Once);

            var markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("00:00", markup);
            Assert.Contains("23:00", markup);
            Assert.Contains("4.2 mm", markup);
            Assert.Contains("12 km/h", markup);
            Assert.True(markup.IndexOf("First hour", StringComparison.Ordinal) < markup.IndexOf("Last hour", StringComparison.Ordinal));
            Assert.Equal(2, hourlyResult.Periods.Count);
        });
    }

    [Fact]
    public async Task UnavailableHourlyDataOffersRetryThatBypassesCache()
    {
        await WithCardAsync(async (card, renderer, html, planner, _) =>
        {
            SetupHourly(planner, new WeatherProviderResult { Periods = [] });
            await ClickAsync(card, renderer, "ShowHourlyForecastAsync", Day);
            Assert.Contains("Hourly forecast is unavailable", await renderer.Dispatcher.InvokeAsync(html.ToHtmlString));

            SetupHourly(planner, new WeatherProviderResult { Periods = [Day with { Summary = "Recovered forecast" }] });
            await ClickAsync(card, renderer, "RefreshHourlyForecastAsync");

            Assert.Contains("Recovered forecast", await renderer.Dispatcher.InvokeAsync(html.ToHtmlString));
            planner.Verify(service => service.GetHourlyForecastAsync(
                It.IsAny<WeatherQuery>(),
                Day.Time.Date,
                It.IsAny<CancellationToken>(),
                true), Times.Once);
        });
    }

    [Fact]
    public async Task CachedHourlyForecastCanBeReplacedWithFreshData()
    {
        await WithCardAsync(async (card, renderer, html, planner, _) =>
        {
            var freshResponse = new TaskCompletionSource<WeatherProviderResult>();
            planner.Setup(service => service.GetHourlyForecastAsync(
                    It.IsAny<WeatherQuery>(),
                    Day.Time.Date,
                    It.IsAny<CancellationToken>(),
                    false))
                .ReturnsAsync(new WeatherProviderResult
                {
                    IsCached = true,
                    Periods = [Day with { Summary = "Cached conditions" }]
                });
            planner.Setup(service => service.GetHourlyForecastAsync(
                    It.IsAny<WeatherQuery>(),
                    Day.Time.Date,
                    It.IsAny<CancellationToken>(),
                    true))
                .Returns(freshResponse.Task);

            await ClickAsync(card, renderer, "ShowHourlyForecastAsync", Day);
            var markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("Cached forecast", markup);
            Assert.Contains("Cached conditions", markup);
            Assert.Contains("Fetch latest hourly forecast from Open-Meteo", markup);

            var refresh = ClickAsync(card, renderer, "RefreshHourlyForecastAsync");
            markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("Loading hourly forecast", markup);
            Assert.DoesNotContain("Cached conditions", markup);
            freshResponse.SetResult(new WeatherProviderResult
            {
                Periods = [Day with { Summary = "Fresh conditions" }]
            });
            await refresh;

            markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("Fresh conditions", markup);
            Assert.DoesNotContain("Cached forecast", markup);
            planner.Verify(service => service.GetHourlyForecastAsync(
                It.IsAny<WeatherQuery>(),
                Day.Time.Date,
                It.IsAny<CancellationToken>(),
                true), Times.Once);
        });
    }

    [Fact]
    public async Task SlowerPreviousSelectionCannotReplaceCurrentDateDetails()
    {
        await WithCardAsync(async (card, renderer, html, planner, _) =>
        {
            var pending = new TaskCompletionSource<WeatherProviderResult>();
            planner.Setup(service => service.GetHourlyForecastAsync(
                    It.IsAny<WeatherQuery>(),
                    Day.Time.Date,
                    It.IsAny<CancellationToken>(),
                    false))
                .Returns(pending.Task);
            var firstClick = ClickAsync(card, renderer, "ShowHourlyForecastAsync", Day);
            Assert.Contains("Loading hourly forecast", await renderer.Dispatcher.InvokeAsync(html.ToHtmlString));

            var nextDay = Day with { Time = Day.Time.AddDays(1), Summary = "Latest selection" };
            planner.Setup(service => service.GetHourlyForecastAsync(
                    It.IsAny<WeatherQuery>(),
                    nextDay.Time.Date,
                    It.IsAny<CancellationToken>(),
                    false))
                .ReturnsAsync(new WeatherProviderResult { Periods = [nextDay] });
            await ClickAsync(card, renderer, "ShowHourlyForecastAsync", nextDay);
            pending.SetResult(new WeatherProviderResult { Periods = [Day with { Summary = "Stale selection" }] });
            await firstClick;

            var markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("Latest selection", markup);
            Assert.DoesNotContain("Stale selection", markup);
        });
    }

    private static void SetupHourly(Mock<IWeatherPlannerService> planner, WeatherProviderResult result) =>
        planner.Setup(service => service.GetHourlyForecastAsync(
                It.IsAny<WeatherQuery>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync(result);

    private static Task ClickAsync(OpenMeteoWeatherCard card, HtmlRenderer renderer, string method, params object[] args) =>
        renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)card).HandleEventAsync(
            new EventCallbackWorkItem((Func<Task>)(() =>
                (Task)typeof(OpenMeteoWeatherCard).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(card, args)!)), null));

    private static KeyedWeatherProviderOptions LoadOpenMeteoOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../MyTransportAppWASM/wwwroot/appsettings.json"))).Build();
        return configuration.GetSection("WeatherProviders:OpenMeteo").Get<KeyedWeatherProviderOptions>()!;
    }

    private static async Task WithCardAsync(Func<OpenMeteoWeatherCard, HtmlRenderer, HtmlRootComponent,
        Mock<IWeatherPlannerService>, Mock<IJSRuntime>, Task> test, WeatherTimeSlice? day = null)
    {
        var options = LoadOpenMeteoOptions();
        var planner = new Mock<IWeatherPlannerService>();
        var js = new Mock<IJSRuntime>();
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton(planner.Object).AddSingleton(js.Object)
            .AddCascadingValue(_ => new LanguageService(new HttpClient()))
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<OpenMeteoWeatherCard>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(OpenMeteoWeatherCard.Options)] = options,
                [nameof(OpenMeteoWeatherCard.Query)] = new WeatherQuery { Latitude = 3.139, Longitude = 101.6869 },
                [nameof(OpenMeteoWeatherCard.Result)] = new WeatherProviderResult { Periods = [day ?? Day] },
                [nameof(OpenMeteoWeatherCard.FetchOutlook)] = true
            })));
        await test(activator.Card!, renderer, html, planner, js);
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public OpenMeteoWeatherCard? Card { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is OpenMeteoWeatherCard card) Card = card;
            return component;
        }
    }

    private sealed class CapturingHandler(Action<Uri> onRequest, string body = "{}") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri != null)
            {
                onRequest(request.RequestUri);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
