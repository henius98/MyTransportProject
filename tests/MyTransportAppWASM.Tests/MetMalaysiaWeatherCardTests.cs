using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Pages.Weather.Components;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;
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
        var beforeRender = CurrentMalaysiaDate();
        var endpoint = await RenderAndCaptureEndpointAsync(
            fetchOutlook: false,
            new WeatherQuery
            {
                LocationId = "Ds001",
                Departure = MalaysiaTime.At(new DateTime(2026, 10, 10)),
                Return = MalaysiaTime.At(new DateTime(2026, 10, 17))
            });
        var afterRender = CurrentMalaysiaDate();
        var dates = GetRequestedDates(endpoint);

        Assert.Equal(dates.Start, dates.End);
        Assert.Contains(dates.Start, new[] { beforeRender, afterRender });
    }

    [Fact]
    public async Task OutlookRequestKeepsSelectedDateRange()
    {
        var endpoint = await RenderAndCaptureEndpointAsync(
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

    private static async Task<Uri> RenderAndCaptureEndpointAsync(bool fetchOutlook, WeatherQuery query)
    {
        Uri? requestedEndpoint = null;
        var weatherPlanner = new Mock<IWeatherPlannerService>();
        weatherPlanner
            .Setup(service => service.FetchProviderDataAsync(
                It.IsAny<WeatherProviderOptions>(),
                It.IsAny<Uri>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Callback<WeatherProviderOptions, Uri, string, CancellationToken, bool>(
                (_, endpoint, _, _, _) => requestedEndpoint = endpoint)
            .ReturnsAsync(new WeatherProviderResult
            {
                Provider = "MetMalaysia",
                Status = "LIVE",
                Periods =
                [
                    new WeatherTimeSlice
                    {
                        Label = "Pagi",
                        Time = DateTimeOffset.UtcNow,
                        Summary = "Tiada hujan"
                    }
                ]
            });

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(weatherPlanner.Object)
            .AddSingleton(new LanguageService(new HttpClient()))
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MetMalaysiaWeatherCard>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(MetMalaysiaWeatherCard.Options)] = new MetMalaysiaWeatherProviderOptions
                {
                    Enabled = true,
                    Name = "MetMalaysia",
                    BaseUrl = BaseUrl,
                    Endpoints = EndpointTemplate
                },
                [nameof(MetMalaysiaWeatherCard.Query)] = query,
                [nameof(MetMalaysiaWeatherCard.Label)] = fetchOutlook ? "Outlook" : "Realtime",
                [nameof(MetMalaysiaWeatherCard.FetchOutlook)] = fetchOutlook
            })));

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
}
