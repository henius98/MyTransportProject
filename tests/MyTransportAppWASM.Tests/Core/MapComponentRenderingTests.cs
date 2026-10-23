using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Pages.Map.Components;

namespace MyTransportAppWASM.Tests;

public class MapComponentRenderingTests
{
    [Fact]
    public async Task LiveRouteOptionsList_RendersRouteDetailsAndBadges()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var routes = new List<EnrichedRoute>
        {
            new()
            {
                OriginalRoute = new GoogleRoute
                {
                    RouteId = 0,
                    DurationSeconds = 1200,
                    EncodedPolyline = "abc"
                },
                HasLiveBus = true,
                LiveTotalDurationSeconds = 900,
                IsAuspicious = true,
                NearestBuses =
                [
                    new LiveBusInfo
                    {
                        RouteId = "T801",
                        DistanceToStopMeters = 350
                    }
                ]
            },
            new()
            {
                OriginalRoute = new GoogleRoute
                {
                    RouteId = 1,
                    DurationSeconds = 1800,
                    EncodedPolyline = "def"
                },
                HasLiveBus = false,
                IsAuspicious = false
            }
        };

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<LiveRouteOptionsList>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LiveRouteOptionsList.RouteOptions)] = routes,
                    [nameof(LiveRouteOptionsList.ActiveRouteId)] = 0
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Option 1", html);
        Assert.Contains("Option 2", html);
        Assert.Contains("🌟 Auspicious", html);
        Assert.Contains("Live", html);
        Assert.Contains("Scheduled", html);
        Assert.Contains("Live ETA: 15 mins", html);
        Assert.Contains("Base ETA: 20 mins", html);
        Assert.Contains("Bus <strong>T801</strong> is 350m away", html);
        Assert.Contains("border-primary", html);
    }

    [Fact]
    public async Task LiveRouteOptionsList_RendersNothingWhenEmpty()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<LiveRouteOptionsList>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LiveRouteOptionsList.RouteOptions)] = (IReadOnlyList<EnrichedRoute>)Array.Empty<EnrichedRoute>(),
                    [nameof(LiveRouteOptionsList.ActiveRouteId)] = -1
                }));
            return component.ToHtmlString();
        });

        Assert.Empty(html.Trim());
    }

    [Fact]
    public async Task StaticRoutePanel_RendersProvidersAndDepartures()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var providers = new List<TransportProvider>
        {
            new()
            {
                Name = "Rapid Bus KL",
                StaticData = new TransportStaticData("https://example.com", "routes.json", "map.geojson", "departures.json")
            },
            new()
            {
                Name = "KTMB",
                StaticData = null
            }
        };

        var routes = new List<StaticRoute>
        {
            new("route-1", "Route One")
        };

        var departures = new List<StaticDeparture>
        {
            new("route-1", "KL Sentral", DateTimeOffset.UtcNow.AddMinutes(10))
        };

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<StaticRoutePanel>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(StaticRoutePanel.TransportProviders)] = providers,
                    [nameof(StaticRoutePanel.SelectedProviderIndex)] = 0,
                    [nameof(StaticRoutePanel.HasSelectedStaticData)] = true,
                    [nameof(StaticRoutePanel.SelectedRouteId)] = "route-1",
                    [nameof(StaticRoutePanel.Routes)] = routes,
                    [nameof(StaticRoutePanel.Status)] = "Scheduled estimates, not live arrivals.",
                    [nameof(StaticRoutePanel.StopName)] = "Test Stop",
                    [nameof(StaticRoutePanel.Departures)] = departures
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Rapid Bus KL", html);
        Assert.DoesNotContain("value=\"1\">KTMB", html);
        Assert.Contains("Route One (route-1)", html);
        Assert.Contains("Test Stop", html);
        Assert.Contains("Scheduled estimates, not live arrivals.", html);
    }
}
