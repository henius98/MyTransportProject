using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using MapPage = MyTransportAppWASM.Features.Transit.Map;

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
    public async Task LiveRouteOptionsList_RendersGoogleStyleScheduleSegmentsAndSanitizesColors()
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
                    DurationSeconds = 1860,
                    LocalizedDuration = "31 min",
                    DepartureTimeText = "9:47 AM",
                    ArrivalTimeText = "10:18 AM",
                    HeadwayMinutes = 10,
                    Segments =
                    [
                        new RouteSegment
                        {
                            TravelMode = "WALKING",
                            DurationSeconds = 360,
                            Instructions = "Walk to Aeon Maluri 1"
                        },
                        new RouteSegment
                        {
                            TravelMode = "TRANSIT",
                            VehicleType = "BUS",
                            LineShortName = "400",
                            LineName = "Damai Perdana",
                            LineColor = "#1565c0",
                            LineTextColor = "javascript:alert(1)",
                            Headsign = "Hab Lebuh Pudu",
                            DepartureStopName = "Aeon Maluri 1 (Opp)",
                            ArrivalStopName = "Bukit Bintang",
                            DepartureTimeText = "9:53 AM",
                            ArrivalTimeText = "10:12 AM",
                            DurationSeconds = 1140,
                            StopCount = 6
                        }
                    ]
                },
                HasLiveBus = false
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

        Assert.Contains("9:47 AM—10:18 AM", html);
        Assert.Contains("31 min", html);
        Assert.Contains("400", html);
        Assert.Contains("#1565c0", html);
        Assert.DoesNotContain("javascript:alert(1)", html);
        Assert.Contains("9:53 AM from Aeon Maluri 1 (Opp)", html);
        Assert.Contains("every 10 min", html);
        Assert.Contains("Details", html);
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
            new("route-1", "KL Sentral", DateTimeOffset.UtcNow.AddMinutes(10)),
            new("route-2", "Unknown Time", null)
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
        Assert.Contains("All routes", html);
        Assert.Contains("Route One (route-1)", html);
        Assert.Contains("Test Stop", html);
        Assert.Contains("Scheduled estimates, not live arrivals.", html);
        Assert.Contains("Time unavailable", html);
    }

    [Fact]
    public async Task Map_CollapsesSearchPanelAndFiltersVehicles_WhenSuggestedRouteSelected()
    {
        var allVehicles = new List<BusLocation>
        {
            new() { VehicleId = "bus-200", RouteId = "200", TransportType = "Bus" },
            new() { VehicleId = "bus-400", RouteId = "400", TransportType = "Bus" },
            new() { VehicleId = "ktmb-pk", RouteId = "Port Klang", TransportType = "KTMB" },
            new() { VehicleId = "erl-37", RouteId = "KLIA Ekspres 37", TransportType = "Rail" }
        };
        var route = new EnrichedRoute
        {
            OriginalRoute = new GoogleRoute
            {
                RouteId = 3,
                DurationSeconds = 20400,
                Segments =
                [
                    new RouteSegment { TravelMode = "TRANSIT", VehicleType = "BUS", LineShortName = "200" },
                    new RouteSegment { TravelMode = "TRANSIT", VehicleType = "HEAVY_RAIL", LineShortName = "Port Klang Line" },
                    new RouteSegment { TravelMode = "TRANSIT", VehicleType = "HEAVY_RAIL", LineShortName = "KLIA Ekspres 37" }
                ]
            }
        };

        var tracking = new Mock<ITransitTrackingService>();
        tracking.Setup(t => t.CachedVehicles).Returns(allVehicles);
        tracking.Setup(t => t.EnrichRoutesAsync(It.IsAny<List<GoogleRoute>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([route]);

        var staticData = new Mock<ITransportStaticDataService>();
        var location = new Mock<ILocationService>();
        var session = new TransitMapSession(
            tracking.Object,
            staticData.Object,
            location.Object,
            Options.Create(new TransitOptions()),
            Options.Create(new DefaultLocationOptions { Latitude = 3.139, Longitude = 101.6869 }));
        await session.EnrichTransitRoutesAsync([route.OriginalRoute]);

        var js = new Mock<IJSRuntime>();
        var mapModule = new Mock<IJSObjectReference>();
        IReadOnlyList<BusLocation>? syncedVehicles = null;
        mapModule
            .Setup(m => m.InvokeAsync<object>("syncMarkers", It.IsAny<object?[]?>()))
            .Callback<string, object?[]?>((_, args) =>
            {
                syncedVehicles = args?[0] as IReadOnlyList<BusLocation>;
            })
            .Returns(ValueTask.FromResult<object>(null!));

        var activator = new MapActivator();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(js.Object)
            .AddSingleton(session)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(location.Object)
            .AddSingleton(new Mock<IPlaceResolutionService>().Object)
            .AddSingleton<IComponentActivator>(activator)
            .AddCascadingValue(_ => new LanguageService(new HttpClient()))
            .AddCascadingValue(_ => new ThemeService(js.Object, new Mock<IUserSettingsService>().Object))
            .BuildServiceProvider();

        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<MapPage>();
            var mapComponent = activator.Component!;
            typeof(MapPage).GetField("mapModule", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(mapComponent, mapModule.Object);
            typeof(MapPage).GetField("origin", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(mapComponent, "UIAM Gombak");
            typeof(MapPage).GetField("destination", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(mapComponent, "KLIA Terminal 1");

            var initialHtml = root.ToHtmlString();
            Assert.DoesNotContain("planner-search-collapsed", initialHtml);
            Assert.DoesNotContain("planner-search-summary", initialHtml);

            var selectRoute = typeof(MapPage).GetMethod("SelectRoute", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)selectRoute.Invoke(mapComponent, [route])!;

            var collapsedHtml = root.ToHtmlString();
            Assert.Contains("planner-search-collapsed", collapsedHtml);
            Assert.Contains("planner-search-summary", collapsedHtml);
            Assert.Contains("UIAM Gombak → KLIA Terminal 1", System.Net.WebUtility.HtmlDecode(collapsedHtml));
            Assert.Contains("Showing Route 4", collapsedHtml);
            Assert.NotNull(syncedVehicles);
            Assert.Equal(["bus-200", "ktmb-pk", "erl-37"], syncedVehicles!.Select(v => v.VehicleId ?? "").ToArray());

            var expandSearch = typeof(MapPage).GetMethod("ExpandSearchPanel", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stateHasChanged = typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            expandSearch.Invoke(mapComponent, null);
            stateHasChanged.Invoke(mapComponent, null);
            var expandedHtml = root.ToHtmlString();
            Assert.DoesNotContain("planner-search-collapsed", expandedHtml);
            Assert.DoesNotContain("planner-search-summary", expandedHtml);

            // Re-collapse via SelectRoute, then clear via ClearRoute to restore all vehicles and expand search.
            await (Task)selectRoute.Invoke(mapComponent, [route])!;
            var clearRoute = typeof(MapPage).GetMethod("ClearRoute", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)clearRoute.Invoke(mapComponent, null)!;

            var clearedHtml = root.ToHtmlString();
            Assert.DoesNotContain("planner-search-collapsed", clearedHtml);
            Assert.NotNull(syncedVehicles);
            Assert.Equal(4, syncedVehicles!.Count);
        });

        session.Dispose();
    }

    private sealed class MapActivator : IComponentActivator
    {
        public MapPage? Component { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var instance = (IComponent)Activator.CreateInstance(componentType)!;
            if (instance is MapPage map) Component = map;
            return instance;
        }
    }
}
