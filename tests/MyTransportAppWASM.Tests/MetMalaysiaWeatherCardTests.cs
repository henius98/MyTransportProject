using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Pages.Weather.Components;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class MetMalaysiaWeatherCardTests
{
    private const string BaseUrl = "https://api.data.gov.my/weather/forecast";
    private const string EndpointTemplate =
        "?contains={location_id}@location__location_id&date_start={date_start}@date&date_end={date_end}@date";

    [Fact]
    public void WeatherProviderSettingsBindMetMalaysiaEndpoint()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WeatherProviders:MetMalaysia:BaseUrl"] = BaseUrl,
                ["WeatherProviders:MetMalaysia:Endpoints"] = EndpointTemplate,
                ["WeatherProviders:OpenMeteo:Endpoints:Realtime"] = "?latitude={lat}"
            })
            .Build();

        var options = configuration.GetSection("WeatherProviders").Get<WeatherOptions>();

        Assert.NotNull(options);
        Assert.Equal(BaseUrl, options.MetMalaysia.BaseUrl);
        Assert.Equal(EndpointTemplate, options.MetMalaysia.Endpoints);
        Assert.Equal("?latitude={lat}", options.OpenMeteo.Endpoints["Realtime"]);
    }

    [Fact]
    public async Task RealtimeRequestOnlyIncludesCurrentMalaysiaDate()
    {
        var beforeRequest = CurrentMalaysiaDate();
        var endpoint = await RequestAndCaptureEndpointAsync(
            fetchOutlook: false,
            new WeatherQuery
            {
                LocationId = "Ds001",
                Departure = MalaysiaTime.At(new DateTime(2026, 10, 10)),
                Return = MalaysiaTime.At(new DateTime(2026, 10, 17))
            });
        var afterRequest = CurrentMalaysiaDate();
        var dates = GetRequestedDates(endpoint);

        Assert.Equal(dates.Start, dates.End);
        Assert.Contains(dates.Start, new[] { beforeRequest, afterRequest });
    }

    [Fact]
    public async Task OutlookRequestKeepsSelectedDateRange()
    {
        var endpoint = await RequestAndCaptureEndpointAsync(
            fetchOutlook: true,
            new WeatherQuery
            {
                LocationId = "Ds001",
                Departure = MalaysiaTime.At(new DateTime(2026, 10, 10)),
                Return = MalaysiaTime.At(new DateTime(2026, 10, 17))
            });
        var dates = GetRequestedDates(endpoint);

        Assert.Equal("2026-10-10", dates.Start);
        Assert.Equal("2026-10-17", dates.End);
    }

    [Fact]
    public async Task CardRendersProvidedResultWithoutSelfFetching()
    {
        var result = new WeatherProviderResult
        {
            Provider = "MetMalaysia",
            Status = "LIVE",
            Periods =
            [
                new WeatherTimeSlice
                {
                    Label = "Pagi",
                    Time = DateTimeOffset.UtcNow,
                    Summary = "Tiada hujan",
                    MinTemperatureC = 24,
                    MaxTemperatureC = 32
                }
            ]
        };

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddCascadingValue(_ => new LanguageService(new HttpClient()))
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<MetMalaysiaWeatherCard>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(MetMalaysiaWeatherCard.Options)] = new MetMalaysiaWeatherProviderOptions
                    {
                        Enabled = true,
                        Name = "MetMalaysia",
                        BaseUrl = BaseUrl,
                        Endpoints = EndpointTemplate
                    },
                    [nameof(MetMalaysiaWeatherCard.Result)] = result
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Tiada hujan", html);
        Assert.Contains("Pagi", html);
    }

    private static async Task<Uri> RequestAndCaptureEndpointAsync(bool fetchOutlook, WeatherQuery query)
    {
        Uri? requestedEndpoint = null;
        var handler = new CapturingHandler(uri => requestedEndpoint = uri);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new WeatherPlannerService(
            new HttpClient(handler),
            cache,
            Options.Create(new CacheExpirationOptions
            {
                Weather = new() { LiveSeconds = 600, OutlookSeconds = 10800, FallbackSeconds = 30 }
            }),
            Options.Create(new WeatherOptions
            {
                MetMalaysia = new MetMalaysiaWeatherProviderOptions
                {
                    Enabled = true,
                    Name = "MetMalaysia",
                    BaseUrl = BaseUrl,
                    Endpoints = EndpointTemplate
                }
            }));

        await service.RefreshProviderAsync("MetMalaysia", query, isOutlook: fetchOutlook);

        return Assert.IsType<Uri>(requestedEndpoint);
    }

    private static (string Start, string End) GetRequestedDates(Uri endpoint)
    {
        var parameters = endpoint.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]));

        return (
            parameters["date_start"].Split('@')[0],
            parameters["date_end"].Split('@')[0]);
    }

    private static string CurrentMalaysiaDate() => MalaysiaTime.Now.ToString("yyyy-MM-dd");

    private sealed class CapturingHandler(Action<Uri> onRequest) : HttpMessageHandler
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
                Content = new StringContent(
                    """[{"date":"2026-10-13T00:00:00Z","morning_forecast":"Tiada hujan"}]""",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
