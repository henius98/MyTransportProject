using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class LiveRoutingServiceTests
{
    private readonly LiveRoutingService _service = new();

    [Fact]
    public async Task EnrichRoutesAsync_DoesNotMatchBus_WhenLineShortNameIsEmpty()
    {
        var route = BuildRoute("");
        var bus = BuildBus("T10");

        var result = await _service.EnrichRoutesAsync([route], [bus]);

        Assert.False(result.Single().HasLiveBus);
        Assert.Empty(result.Single().NearestBuses);
    }

    [Fact]
    public async Task EnrichRoutesAsync_DoesNotUseSubstringRouteMatches()
    {
        var route = BuildRoute("10");
        var bus = BuildBus("T10");

        var result = await _service.EnrichRoutesAsync([route], [bus]);

        Assert.False(result.Single().HasLiveBus);
    }

    [Theory]
    [InlineData("T10", "T10")]
    [InlineData("T-10", "t 10")]
    public async Task EnrichRoutesAsync_MatchesNormalizedRouteNames(string lineName, string routeId)
    {
        var route = BuildRoute(lineName);
        var bus = BuildBus(routeId);

        var result = await _service.EnrichRoutesAsync([route], [bus]);

        Assert.True(result.Single().HasLiveBus);
        Assert.Single(result.Single().NearestBuses);
    }

    [Fact]
    public async Task EnrichRoutesAsync_SelectsNearestMatchingBus()
    {
        var route = BuildRoute("T10");
        var farBus = BuildBus("T10") with
        {
            VehicleId = "far",
            Lat = 3.30f,
            Lng = 101.80f
        };
        var nearBus = BuildBus("t-10") with
        {
            VehicleId = "near",
            Lat = 3.1391f,
            Lng = 101.6871f
        };

        var result = await _service.EnrichRoutesAsync([route], [farBus, nearBus]);

        var selectedBus = Assert.Single(result.Single().NearestBuses);
        Assert.Equal("near", selectedBus.VehicleId);
        Assert.True(selectedBus.DistanceToStopMeters < 20);
    }

    [Fact]
    public async Task EnrichRoutesAsync_RanksLiveRouteBeforeScheduledRoute()
    {
        var scheduledRoute = BuildRoute("T20") with { RouteId = 0 };
        var liveRoute = BuildRoute("T10") with { RouteId = 1 };

        var result = await _service.EnrichRoutesAsync([scheduledRoute, liveRoute], [BuildBus("T10")]);

        Assert.Equal(1, result[0].OriginalRoute.RouteId);
        Assert.True(result[0].HasLiveBus);
        Assert.False(result[1].HasLiveBus);
    }

    private static GoogleRoute BuildRoute(string lineShortName) => new()
    {
        DurationSeconds = 600,
        TransitSteps =
        [
            new TransitStep
            {
                LineShortName = lineShortName,
                DepartureLat = 3.139,
                DepartureLng = 101.687
            }
        ]
    };

    private static BusLocation BuildBus(string routeId) => new()
    {
        RouteId = routeId,
        VehicleId = "vehicle-1",
        Lat = 3.14f,
        Lng = 101.688f
    };
}
