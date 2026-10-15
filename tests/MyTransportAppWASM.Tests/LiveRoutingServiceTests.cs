using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class LiveRoutingServiceTests
{
  [Fact]
  public void EnrichRoutes_ThrowsOnNullArgs()
  {
    var service = new LiveRoutingService();
    Assert.Throws<ArgumentNullException>(() => service.EnrichRoutes(null!, []));
    Assert.Throws<ArgumentNullException>(() => service.EnrichRoutes([], null!));
  }

  [Fact]
  public void EnrichRoutes_EmptyRoutes_ReturnsEmpty()
  {
    var service = new LiveRoutingService();
    var result = service.EnrichRoutes([], []);
    Assert.Empty(result);
  }

  [Fact]
  public void EnrichRoutes_MatchesLiveVehicleAndRanksRoutes()
  {
    var service = new LiveRoutingService();

    var route1 = new GoogleRoute
    {
      RouteId = 0,
      DurationSeconds = 1800,
      TransitSteps =
      [
        new TransitStep
        {
          LineShortName = "T801",
          DepartureLat = 3.139,
          DepartureLng = 101.6869
        }
      ]
    };

    var route2 = new GoogleRoute
    {
      RouteId = 1,
      DurationSeconds = 1200,
      TransitSteps =
      [
        new TransitStep
        {
          LineShortName = "Unmatched-999",
          DepartureLat = 3.139,
          DepartureLng = 101.6869
        }
      ]
    };

    var vehicles = new List<BusLocation>
    {
      new()
      {
        VehicleId = "BUS-01",
        RouteId = "T801",
        Lat = 3.140f,
        Lng = 101.6870f,
        TransportType = "Bus"
      }
    };

    var enriched = service.EnrichRoutes([route2, route1], vehicles);

    Assert.Equal(2, enriched.Count);
    // route1 has live bus, so it ranks first despite route2 having shorter original duration
    Assert.Equal(0, enriched[0].OriginalRoute.RouteId);
    Assert.True(enriched[0].HasLiveBus);
    Assert.Single(enriched[0].NearestBuses);
    Assert.Equal("BUS-01", enriched[0].NearestBuses[0].VehicleId);
    Assert.True(enriched[0].LiveTotalDurationSeconds > route1.DurationSeconds);

    Assert.Equal(1, enriched[1].OriginalRoute.RouteId);
    Assert.False(enriched[1].HasLiveBus);
  }

  [Fact]
  public void EnrichRoutes_MultiLegRoute_OnlyAddsWaitTimeToFirstStep()
  {
    var service = new LiveRoutingService();

    var route = new GoogleRoute
    {
      RouteId = 0,
      DurationSeconds = 1800,
      TransitSteps =
      [
        new TransitStep
        {
          LineShortName = "T801",
          DepartureLat = 3.139,
          DepartureLng = 101.6869
        },
        new TransitStep
        {
          LineShortName = "T802",
          DepartureLat = 3.149,
          DepartureLng = 101.6969
        }
      ]
    };

    var vehicles = new List<BusLocation>
    {
      new()
      {
        VehicleId = "BUS-01",
        RouteId = "T801",
        Lat = 3.140f,
        Lng = 101.6870f
      },
      new()
      {
        VehicleId = "BUS-02",
        RouteId = "T802",
        Lat = 3.150f,
        Lng = 101.6970f
      }
    };

    var enriched = service.EnrichRoutes([route], vehicles);

    Assert.Single(enriched);
    Assert.Equal(2, enriched[0].NearestBuses.Count);
    // LiveTotalDurationSeconds should only add first bus's EstimatedArrivalSeconds, not both
    var firstBusWait = enriched[0].NearestBuses[0].EstimatedArrivalSeconds;
    var secondBusWait = enriched[0].NearestBuses[1].EstimatedArrivalSeconds;
    Assert.Equal(1800 + firstBusWait, enriched[0].LiveTotalDurationSeconds);
    Assert.NotEqual(1800 + firstBusWait + secondBusWait, enriched[0].LiveTotalDurationSeconds);
  }

  [Fact]
  public void EnrichRoutes_DoesNotMatchBus_WhenLineShortNameIsEmpty()
  {
    var service = new LiveRoutingService();
    var route = BuildRoute("");
    var bus = BuildBus("T10");

    var result = service.EnrichRoutes([route], [bus]);

    Assert.False(result.Single().HasLiveBus);
    Assert.Empty(result.Single().NearestBuses);
  }

  [Fact]
  public void EnrichRoutes_DoesNotUseSubstringRouteMatches()
  {
    var service = new LiveRoutingService();
    var route = BuildRoute("10");
    var bus = BuildBus("T10");

    var result = service.EnrichRoutes([route], [bus]);

    Assert.False(result.Single().HasLiveBus);
  }

  [Theory]
  [InlineData("T10", "T10")]
  [InlineData("T-10", "t 10")]
  public void EnrichRoutes_MatchesNormalizedRouteNames(string lineName, string routeId)
  {
    var service = new LiveRoutingService();
    var route = BuildRoute(lineName);
    var bus = BuildBus(routeId);

    var result = service.EnrichRoutes([route], [bus]);

    Assert.True(result.Single().HasLiveBus);
    Assert.Single(result.Single().NearestBuses);
  }

  [Fact]
  public void EnrichRoutes_SelectsNearestMatchingBus()
  {
    var service = new LiveRoutingService();
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

    var result = service.EnrichRoutes([route], [farBus, nearBus]);

    var selectedBus = Assert.Single(result.Single().NearestBuses);
    Assert.Equal("near", selectedBus.VehicleId);
    Assert.True(selectedBus.DistanceToStopMeters < 20);
  }

  [Fact]
  public void EnrichRoutes_RanksLiveRouteBeforeScheduledRoute()
  {
    var service = new LiveRoutingService();
    var scheduledRoute = BuildRoute("T20") with { RouteId = 0 };
    var liveRoute = BuildRoute("T10") with { RouteId = 1 };

    var result = service.EnrichRoutes([scheduledRoute, liveRoute], [BuildBus("T10")]);

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
