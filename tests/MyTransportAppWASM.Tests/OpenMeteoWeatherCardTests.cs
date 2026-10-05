using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

            var request = Assert.Single(planner.Invocations, call => ((Uri)call.Arguments[1]).Query.Contains("daily="));
            var url = ((Uri)request.Arguments[1]).AbsoluteUri;
            Assert.Contains("precipitation_sum", url);
            Assert.Contains("temperature_2m_min", url);
            Assert.Contains("wind_speed_10m_max", url);
            Assert.Contains("uv_index_max", url);
            Assert.Contains("sunrise,sunset", url);

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
        await WithCardAsync(async (card, renderer, html, planner, js) =>
        {
            Assert.Contains("aria-haspopup=\"dialog\"", await renderer.Dispatcher.InvokeAsync(html.ToHtmlString));
            SetupHourly(planner, new WeatherProviderResult
            {
                Periods =
                [
                    Day with { Time = Day.Time.AddHours(23), Summary = "Last hour", Wind = "12 km/h" },
                    Day with { Time = Day.Time.AddDays(1), Summary = "Next day" },
                    Day with { Time = Day.Time.ToUniversalTime(), Summary = "First hour", PrecipitationMm = 4.2 },
                    Day with { Time = Day.Time.AddHours(-1), Summary = "Previous day" },
                    Day with { IsPlaceholder = true, Summary = "Placeholder" }
                ]
            });

            await ClickAsync(card, renderer, "ShowHourlyForecastAsync", Day);

            var request = Assert.Single(planner.Invocations, call => ((Uri)call.Arguments[1]).Query.Contains("hourly="));
            var url = Uri.UnescapeDataString(((Uri)request.Arguments[1]).AbsoluteUri);
            Assert.Contains($"start_date={Day.Time:yyyy-MM-dd}&end_date={Day.Time:yyyy-MM-dd}", url);
            Assert.Contains("latitude=3.1390&longitude=101.6869", url);
            Assert.Contains($"timezone={MalaysiaTime.IanaTimeZone}", url);
            Assert.DoesNotContain("forecast_hours", url);
            js.Verify(runtime => runtime.InvokeAsync<It.IsAnyType>("dialogHelper.showModal", It.IsAny<object[]>()), Times.Once);

            var markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("00:00", markup);
            Assert.Contains("23:00", markup);
            Assert.Contains("4.2 mm", markup);
            Assert.Contains("12 km/h", markup);
            Assert.True(markup.IndexOf("First hour", StringComparison.Ordinal) < markup.IndexOf("Last hour", StringComparison.Ordinal));
            Assert.DoesNotContain("Next day", markup);
            Assert.DoesNotContain("Previous day", markup);
            Assert.DoesNotContain("Placeholder", markup);
        });
    }

    [Fact]
    public async Task UnavailableHourlyDataOffersRetryThatBypassesCache()
    {
        await WithCardAsync(async (card, renderer, html, planner, _) =>
        {
            SetupHourly(planner, new WeatherProviderResult { Periods = [Day with { IsPlaceholder = true }] });
            await ClickAsync(card, renderer, "ShowHourlyForecastAsync", Day);
            Assert.Contains("Hourly forecast is unavailable", await renderer.Dispatcher.InvokeAsync(html.ToHtmlString));

            SetupHourly(planner, new WeatherProviderResult { Periods = [Day with { Summary = "Recovered forecast" }] });
            await ClickAsync(card, renderer, "RefreshHourlyForecastAsync");

            Assert.Contains("Recovered forecast", await renderer.Dispatcher.InvokeAsync(html.ToHtmlString));
            planner.Verify(service => service.FetchProviderDataAsync(It.IsAny<WeatherProviderOptions>(),
                It.Is<Uri>(uri => uri.Query.Contains("hourly=")), "Outlook", It.IsAny<CancellationToken>(), true), Times.Once);
        });
    }

    [Fact]
    public async Task CachedHourlyForecastCanBeReplacedWithFreshData()
    {
        await WithCardAsync(async (card, renderer, html, planner, _) =>
        {
            var freshResponse = new TaskCompletionSource<WeatherProviderResult>();
            planner.Setup(service => service.FetchProviderDataAsync(It.IsAny<WeatherProviderOptions>(),
                    It.Is<Uri>(uri => uri.Query.Contains("hourly=")), "Outlook", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(new WeatherProviderResult
                {
                    IsCached = true,
                    Periods = [Day with { Summary = "Cached conditions" }]
                });
            planner.Setup(service => service.FetchProviderDataAsync(It.IsAny<WeatherProviderOptions>(),
                    It.Is<Uri>(uri => uri.Query.Contains("hourly=")), "Outlook", It.IsAny<CancellationToken>(), true))
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
            planner.Verify(service => service.FetchProviderDataAsync(It.IsAny<WeatherProviderOptions>(),
                It.Is<Uri>(uri => uri.Query.Contains("hourly=")), "Outlook", It.IsAny<CancellationToken>(), true), Times.Once);
        });
    }

    [Fact]
    public async Task SlowerPreviousSelectionCannotReplaceCurrentDateDetails()
    {
        await WithCardAsync(async (card, renderer, html, planner, _) =>
        {
            var pending = new TaskCompletionSource<WeatherProviderResult>();
            planner.Setup(service => service.FetchProviderDataAsync(It.IsAny<WeatherProviderOptions>(),
                    It.Is<Uri>(uri => uri.Query.Contains("hourly=")), "Outlook", It.IsAny<CancellationToken>(), false))
                .Returns(pending.Task);
            var firstClick = ClickAsync(card, renderer, "ShowHourlyForecastAsync", Day);
            Assert.Contains("Loading hourly forecast", await renderer.Dispatcher.InvokeAsync(html.ToHtmlString));

            var nextDay = Day with { Time = Day.Time.AddDays(1), Summary = "Latest selection" };
            SetupHourly(planner, new WeatherProviderResult { Periods = [nextDay] });
            await ClickAsync(card, renderer, "ShowHourlyForecastAsync", nextDay);
            pending.SetResult(new WeatherProviderResult { Periods = [Day with { Summary = "Stale selection" }] });
            await firstClick;

            var markup = await renderer.Dispatcher.InvokeAsync(html.ToHtmlString);
            Assert.Contains("Latest selection", markup);
            Assert.DoesNotContain("Stale selection", markup);
        });
    }

    private static void SetupHourly(Mock<IWeatherPlannerService> planner, WeatherProviderResult result) =>
        planner.Setup(service => service.FetchProviderDataAsync(It.IsAny<WeatherProviderOptions>(),
                It.Is<Uri>(uri => uri.Query.Contains("hourly=")), "Outlook", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(result);

    private static Task ClickAsync(OpenMeteoWeatherCard card, HtmlRenderer renderer, string method, params object[] args) =>
        renderer.Dispatcher.InvokeAsync(() => ((IHandleEvent)card).HandleEventAsync(
            new EventCallbackWorkItem((Func<Task>)(() =>
                (Task)typeof(OpenMeteoWeatherCard).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(card, args)!)), null));

    private static async Task WithCardAsync(Func<OpenMeteoWeatherCard, HtmlRenderer, HtmlRootComponent,
        Mock<IWeatherPlannerService>, Mock<IJSRuntime>, Task> test, WeatherTimeSlice? day = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../MyTransportAppWASM/wwwroot/appsettings.json"))).Build();
        var options = configuration.GetSection("WeatherProviders:OpenMeteo").Get<KeyedWeatherProviderOptions>()!;
        var planner = new Mock<IWeatherPlannerService>();
        planner.Setup(service => service.FetchProviderDataAsync(It.IsAny<WeatherProviderOptions>(), It.IsAny<Uri>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new WeatherProviderResult { Periods = [day ?? Day] });
        var js = new Mock<IJSRuntime>();
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton(planner.Object).AddSingleton(js.Object)
            .AddSingleton(new LanguageService(new HttpClient()))
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<OpenMeteoWeatherCard>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(OpenMeteoWeatherCard.Options)] = options,
                [nameof(OpenMeteoWeatherCard.Query)] = new WeatherQuery { Latitude = 3.139, Longitude = 101.6869 },
                [nameof(OpenMeteoWeatherCard.FetchOutlook)] = true,
                [nameof(OpenMeteoWeatherCard.Label)] = "Outlook"
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
}
