namespace MyTransportAppWASM.Tests;

public class LiveRoutingServiceTests
{
  [Fact]
  public void EnrichRoutes_ThrowsOnNullArgs()
  {
    Assert.Throws<ArgumentNullException>(() => LiveRouting.EnrichRoutes(null!, []));
    Assert.Throws<ArgumentNullException>(() => LiveRouting.EnrichRoutes([], null!));
  }

  [Fact]
  public void EnrichRoutes_EmptyRoutes_ReturnsEmpty()
  {
    var result = LiveRouting.EnrichRoutes([], []);
    Assert.Empty(result);
  }

  [Fact]
  public void EnrichRoutes_MatchesLiveVehicleAndRanksRoutes()
  {
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

    var enriched = LiveRouting.EnrichRoutes([route2, route1], vehicles);

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

    var enriched = LiveRouting.EnrichRoutes([route], vehicles);

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
    var route = BuildRoute("");
    var bus = BuildBus("T10");

    var result = LiveRouting.EnrichRoutes([route], [bus]);

    Assert.False(result.Single().HasLiveBus);
    Assert.Empty(result.Single().NearestBuses);
  }

  [Fact]
  public void EnrichRoutes_DoesNotUseSubstringRouteMatches()
  {
    var route = BuildRoute("10");
    var bus = BuildBus("T10");

    var result = LiveRouting.EnrichRoutes([route], [bus]);

    Assert.False(result.Single().HasLiveBus);
  }

  [Theory]
  [InlineData("T10", "T10")]
  [InlineData("T-10", "t 10")]
  public void EnrichRoutes_MatchesNormalizedRouteNames(string lineName, string routeId)
  {
    var route = BuildRoute(lineName);
    var bus = BuildBus(routeId);

    var result = LiveRouting.EnrichRoutes([route], [bus]);

    Assert.True(result.Single().HasLiveBus);
    Assert.Single(result.Single().NearestBuses);
  }

  [Fact]
  public void EnrichRoutes_SelectsNearestMatchingBus()
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

    var result = LiveRouting.EnrichRoutes([route], [farBus, nearBus]);

    var selectedBus = Assert.Single(result.Single().NearestBuses);
    Assert.Equal("near", selectedBus.VehicleId);
    Assert.True(selectedBus.DistanceToStopMeters < 20);
  }

  [Fact]
  public void EnrichRoutes_RanksLiveRouteBeforeScheduledRoute()
  {
    var scheduledRoute = BuildRoute("T20") with { RouteId = 0 };
    var liveRoute = BuildRoute("T10") with { RouteId = 1 };

    var result = LiveRouting.EnrichRoutes([scheduledRoute, liveRoute], [BuildBus("T10")]);

    Assert.Equal(1, result[0].OriginalRoute.RouteId);
    Assert.True(result[0].HasLiveBus);
    Assert.False(result[1].HasLiveBus);
  }

  [Fact]
  public void FilterVehiclesForRoute_ReturnsOnlyInvolvedTransports()
  {
    var route = new GoogleRoute
    {
      RouteId = 3,
      Segments =
      [
        new RouteSegment { TravelMode = "WALKING" },
        new RouteSegment { TravelMode = "TRANSIT", VehicleType = "BUS", LineShortName = "200", LineName = "Ulu Klang" },
        new RouteSegment { TravelMode = "WALKING" },
        new RouteSegment { TravelMode = "TRANSIT", VehicleType = "HEAVY_RAIL", LineShortName = "Port Klang Line", LineName = "Port Klang Line" },
        new RouteSegment { TravelMode = "TRANSIT", VehicleType = "HEAVY_RAIL", LineShortName = "KLIA Ekspres 37", LineName = "KLIA Ekspres" }
      ]
    };

    var vehicles = new List<BusLocation>
    {
      BuildBus("200") with { VehicleId = "bus-200" },
      BuildBus("400") with { VehicleId = "bus-400" },
      BuildBus("2000") with { VehicleId = "bus-2000" },
      BuildBus("T200") with { VehicleId = "bus-t200" },
      BuildBus("Port Klang") with { VehicleId = "ktmb-pk", TransportType = "KTMB" },
      BuildBus("Seremban") with { VehicleId = "ktmb-seremban", TransportType = "KTMB" },
      BuildBus("KLIA Ekspres 37") with { VehicleId = "erl-37", TransportType = "Rail" },
      BuildBus("") with { VehicleId = "no-route" }
    };

    var filtered = LiveRouting.FilterVehiclesForRoute(route, vehicles);

    Assert.Equal(["bus-200", "ktmb-pk", "erl-37"], filtered.Select(v => v.VehicleId ?? "").ToArray());
  }

  [Fact]
  public void FilterVehiclesForRoute_WalkingOnlyRoute_ReturnsEmpty()
  {
    var route = new GoogleRoute
    {
      RouteId = 0,
      Segments = [new RouteSegment { TravelMode = "WALKING", DurationSeconds = 300 }]
    };

    var filtered = LiveRouting.FilterVehiclesForRoute(route, [BuildBus("200")]);

    Assert.Empty(filtered);
  }

  [Fact]
  public void EnrichRoutes_MatchesMappedRouteShortName_AndUsesShortNameInLiveBusInfo()
  {
    var route = BuildRoute("420");
    var bus = BuildBus("U4200") with { RouteShortName = "420" };

    var result = LiveRouting.EnrichRoutes([route], [bus]);

    var enriched = Assert.Single(result);
    Assert.True(enriched.HasLiveBus);
    var liveBus = Assert.Single(enriched.NearestBuses);
    Assert.Equal("420", liveBus.RouteId);
  }

  [Fact]
  public void EnrichRoutes_StillMatchesRawRouteId_WhenRouteShortNameDiffers()
  {
    var route = BuildRoute("U4200");
    var bus = BuildBus("U4200") with { RouteShortName = "420" };

    var result = LiveRouting.EnrichRoutes([route], [bus]);

    Assert.True(Assert.Single(result).HasLiveBus);
  }

  [Fact]
  public void EnrichRoutes_FallsBackToLineName_WhenLineShortNameIsEmpty()
  {
    var route = new GoogleRoute
    {
      DurationSeconds = 600,
      TransitSteps =
      [
        new TransitStep
        {
          LineShortName = "",
          LineName = "Sunway Line",
          DepartureLat = 3.139,
          DepartureLng = 101.687
        }
      ]
    };
    var bus = BuildBus("B1000") with { RouteShortName = "SUNWAY LINE" };

    var result = LiveRouting.EnrichRoutes([route], [bus]);

    var enriched = Assert.Single(result);
    Assert.True(enriched.HasLiveBus);
    Assert.Equal("SUNWAY LINE", Assert.Single(enriched.NearestBuses).RouteId);
  }

  [Fact]
  public void FilterVehiclesForRoute_MatchesMappedRouteShortName()
  {
    var route = new GoogleRoute
    {
      RouteId = 0,
      Segments = [new RouteSegment { TravelMode = "TRANSIT", VehicleType = "BUS", LineShortName = "420" }]
    };

    var filtered = LiveRouting.FilterVehiclesForRoute(route,
    [
      BuildBus("U4200") with { VehicleId = "bus-420", RouteShortName = "420" },
      BuildBus("U4000") with { VehicleId = "bus-400", RouteShortName = "400" }
    ]);

    Assert.Equal("bus-420", Assert.Single(filtered).VehicleId);
  }

  [Theory]
  [InlineData("T155 Inbound")]
  [InlineData("T155 Outbound")]
  public void EnrichAndFilter_MatchesDirectionalRouteSuffixes(string vehicleRouteId)
  {
    var route = new GoogleRoute
    {
      RouteId = 0,
      DurationSeconds = 600,
      TransitSteps = [new TransitStep { LineShortName = "T155", DepartureLat = 3.139, DepartureLng = 101.687 }],
      Segments = [new RouteSegment { TravelMode = "TRANSIT", VehicleType = "BUS", LineShortName = "T155" }]
    };
    var bus = BuildBus(vehicleRouteId) with { VehicleId = "bus-t155" };

    var enriched = Assert.Single(LiveRouting.EnrichRoutes([route], [bus]));
    Assert.True(enriched.HasLiveBus);
    Assert.Equal("bus-t155", Assert.Single(enriched.NearestBuses).VehicleId);

    var filtered = LiveRouting.FilterVehiclesForRoute(route, [bus, BuildBus("T156") with { VehicleId = "other" }]);
    Assert.Equal("bus-t155", Assert.Single(filtered).VehicleId);
  }

  [Fact]
  public void EnrichRoutes_StripsTrailingSuffixAndFallsBackToLineName()
  {
    var route = new GoogleRoute
    {
      RouteId = 0,
      DurationSeconds = 600,
      TransitSteps =
      [
        new TransitStep
        {
          LineShortName = "Port Klang Line",
          LineName = "Port Klang Line",
          DepartureLat = 3.139,
          DepartureLng = 101.687
        },
        new TransitStep
        {
          LineShortName = "400",
          LineName = "400",
          DepartureLat = 3.139,
          DepartureLng = 101.687
        },
        new TransitStep
        {
          LineShortName = "Express",
          LineName = "Sunway Line",
          DepartureLat = 3.139,
          DepartureLng = 101.687
        }
      ]
    };

    var vehicles = new List<BusLocation>
    {
      BuildBus("Port Klang") with { VehicleId = "ktmb-pk", TransportType = "KTMB" },
      BuildBus("400 Inbound") with { VehicleId = "bus-400", TransportType = "Bus" },
      BuildBus("Sunway") with { VehicleId = "brt-sunway", TransportType = "BRT" }
    };

    var enriched = Assert.Single(LiveRouting.EnrichRoutes([route], vehicles));
    Assert.True(enriched.HasLiveBus);
    Assert.Equal(["ktmb-pk", "bus-400", "brt-sunway"], enriched.NearestBuses.Select(b => b.VehicleId).ToArray());
    Assert.Equal("KTMB", enriched.NearestBuses[0].TransportType);
  }

  [Fact]
  public void FilterVehiclesForStaticRoute_MatchesProviderAndRouteIdOrNameOnly()
  {
    var staticRoutes = new List<StaticRoute>
    {
      new("30000019", "401"),
      new("U4200", "420"),
      new("30000009", null, "T808")
    };

    var vehicles = new List<BusLocation>
    {
      BuildBus("30000019") with { VehicleId = "kl-id", ProviderName = "Rapid Bus KL", TransportType = "Bus" },
      BuildBus("401") with { VehicleId = "kl-short", ProviderName = "Rapid Bus KL", TransportType = "Bus" },
      BuildBus("other") with { VehicleId = "kl-mapped", ProviderName = "Rapid Bus KL", RouteShortName = "401", TransportType = "Bus" },
      BuildBus("T808") with { VehicleId = "kl-long", ProviderName = "Rapid Bus KL", TransportType = "Bus" },
      BuildBus("4010") with { VehicleId = "kl-prefix", ProviderName = "Rapid Bus KL", TransportType = "Bus" },
      BuildBus("401") with { VehicleId = "penang-same-route", ProviderName = "Rapid Bus Penang", TransportType = "Bus" },
      BuildBus("401") with { VehicleId = "no-provider", ProviderName = null, TransportType = "Bus" }
    };

    var filtered = LiveRouting.FilterVehiclesForStaticRoute("Rapid Bus KL", "30000019", staticRoutes, vehicles);

    Assert.Equal(["kl-id", "kl-short", "kl-mapped"], filtered.Select(v => v.VehicleId ?? "").ToArray());
    Assert.Equal(
      ["kl-long"],
      LiveRouting.FilterVehiclesForStaticRoute("Rapid Bus KL", "30000009", staticRoutes, vehicles)
        .Select(v => v.VehicleId ?? "")
        .ToArray());
    Assert.Empty(LiveRouting.FilterVehiclesForStaticRoute("Rapid Bus KL", "", staticRoutes, vehicles));
    Assert.Empty(LiveRouting.FilterVehiclesForStaticRoute("", "30000019", staticRoutes, vehicles));
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
