using Microsoft.Extensions.Options;
using Moq;

namespace MyTransportAppWASM.Tests;

public class TransitMapSessionTests
{
  private static readonly TransportStaticData StaticSource =
    new("https://worker.example/rapid", "/routes", "/map", "/departures");

  private static TransitMapSession CreateSession(
    Mock<ITransitTrackingService>? tracking = null,
    Mock<ITransportStaticDataService>? staticData = null,
    Mock<ILocationService>? location = null,
    IReadOnlyList<TransportProvider>? providers = null)
  {
    tracking ??= new Mock<ITransitTrackingService>();
    staticData ??= new Mock<ITransportStaticDataService>();
    location ??= new Mock<ILocationService>();
    providers ??= [new TransportProvider("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30, "Bus", StaticSource)];

    return new TransitMapSession(
      tracking.Object,
      staticData.Object,
      location.Object,
      Options.Create(new TransitOptions
      {
        Providers = providers,
        Polling = new TransitPollingOptions { IntervalSeconds = 15 }
      }),
      Options.Create(new DefaultLocationOptions { Latitude = 3.1390, Longitude = 101.6869 }));
  }

  [Fact]
  public async Task SelectStaticProviderAsync_LoadsRoutesWithoutAutoRenderingMapAndIgnoresStaleSwitch()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    var firstGate = new TaskCompletionSource();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .Returns(async () =>
      {
        await firstGate.Task;
        return [new StaticRoute("R1", "Route 1")];
      });

    using var session = CreateSession(staticData: staticData);

    var firstLoad = session.SelectStaticProviderAsync(0);
    // Switch provider back to -1 before the slow async load completes
    Assert.Null(await session.SelectStaticProviderAsync(-1));
    firstGate.TrySetResult();

    Assert.Null(await firstLoad);
    Assert.Equal(-1, session.SelectedStaticProviderIndex);
    Assert.Empty(session.StaticRoutes);
    staticData.Verify(s => s.GetMapAsync(StaticSource, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task SelectStaticProviderAsync_IgnoresStaleCompletionAfterAtoBtoASwitch()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    var firstGate = new TaskCompletionSource();
    var callCount = 0;
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .Returns(async () =>
      {
        if (Interlocked.Increment(ref callCount) == 1)
        {
          await firstGate.Task;
          return [new StaticRoute("R1", "Stale Route 1")];
        }
        return [new StaticRoute("R2", "Fresh Route 2")];
      });

    using var session = CreateSession(staticData: staticData);

    var firstLoad = session.SelectStaticProviderAsync(0);
    Assert.Null(await session.SelectStaticProviderAsync(-1));
    Assert.Null(await session.SelectStaticProviderAsync(0));
    firstGate.TrySetResult();

    Assert.Null(await firstLoad);
    Assert.Equal(0, session.SelectedStaticProviderIndex);
    var route = Assert.Single(session.StaticRoutes);
    Assert.Equal("R2", route.Id);
    staticData.Verify(s => s.GetMapAsync(StaticSource, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task SelectStaticProviderAndLoadDepartures_UpdatesSessionState()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("R1", "Route 1")]);
    staticData.Setup(s => s.GetDeparturesAsync(StaticSource, "", "STOP1", It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticDeparture("R1", "KL Sentral", null)]);

    using var session = CreateSession(staticData: staticData);

    var geoJson = await session.SelectStaticProviderAsync(0);
    Assert.Null(geoJson);
    Assert.Single(session.StaticRoutes);
    Assert.Equal("Select a route to view its map.", session.StaticStatus);
    staticData.Verify(s => s.GetMapAsync(StaticSource, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    Assert.True(await session.LoadStaticDeparturesAsync("STOP1", "", CancellationToken.None));
    Assert.Equal("STOP1", session.StaticStopName);
    Assert.Single(session.StaticDepartures);
    Assert.Equal("Scheduled estimates, not live arrivals.", session.StaticStatus);
  }

  [Fact]
  public async Task EnrichTransitRoutesAsync_SelectsFirstRouteAndUpdatesStatus()
  {
    var tracking = new Mock<ITransitTrackingService>();
    var enriched = new List<EnrichedRoute>
    {
      new() { OriginalRoute = new GoogleRoute { RouteId = 2 }, HasLiveBus = true },
      new() { OriginalRoute = new GoogleRoute { RouteId = 0 }, HasLiveBus = false }
    };
    tracking.Setup(t => t.EnrichRoutesAsync(It.IsAny<List<GoogleRoute>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(enriched);

    using var session = CreateSession(tracking: tracking);

    var routes = await session.EnrichTransitRoutesAsync([new GoogleRoute { RouteId = 0 }, new GoogleRoute { RouteId = 2 }]);

    Assert.Equal(2, routes.Count);
    Assert.Equal(2, session.ActiveRouteId);
    Assert.Equal("Found 2 routes.", session.LocationStatus);
  }

  [Fact]
  public void LocationAndRouteStatusTransitions_AreOwnedBySession()
  {
    using var session = CreateSession();

    Assert.False(session.TryBeginRouteCalculation(" ", "KL Sentral"));
    Assert.Equal("Please enter origin and destination.", session.LocationStatus);

    Assert.True(session.TryBeginRouteCalculation("KLCC", "KL Sentral"));
    Assert.Equal("Calculating live routes...", session.LocationStatus);

    session.ReportGpsLoading();
    Assert.Equal("Loading GPS...", session.LocationStatus);

    session.ReportLiveLocation();
    Assert.Equal("Live", session.LocationStatus);

    session.ReportMapError();
    Assert.Equal("Map error", session.LocationStatus);
    Assert.True(session.IsErrorStatus);

    session.ReportRouteFailure();
    Assert.Equal("Route calculation failed.", session.LocationStatus);
    Assert.True(session.IsErrorStatus);
  }

  [Fact]
  public async Task SetActiveRoute_WithFilterVehicles_FiltersDisplayedAndRefreshedVehiclesUntilCleared()
  {
    var allVehicles = new List<BusLocation>
    {
      new() { VehicleId = "bus-200", RouteId = "200", TransportType = "Bus" },
      new() { VehicleId = "bus-400", RouteId = "400", TransportType = "Bus" },
      new() { VehicleId = "ktmb-pk", RouteId = "Port Klang", TransportType = "KTMB" },
      new() { VehicleId = "erl-37", RouteId = "KLIA Ekspres 37", TransportType = "Rail" }
    };

    var routeA = new EnrichedRoute
    {
      OriginalRoute = new GoogleRoute
      {
        RouteId = 3,
        Segments =
        [
          new RouteSegment { TravelMode = "TRANSIT", LineShortName = "200" },
          new RouteSegment { TravelMode = "TRANSIT", LineShortName = "Port Klang Line" },
          new RouteSegment { TravelMode = "TRANSIT", LineShortName = "KLIA Ekspres 37" }
        ]
      }
    };
    var routeB = new EnrichedRoute
    {
      OriginalRoute = new GoogleRoute
      {
        RouteId = 1,
        Segments = [new RouteSegment { TravelMode = "TRANSIT", LineShortName = "400" }]
      }
    };

    var tracking = new Mock<ITransitTrackingService>();
    tracking.Setup(t => t.CachedVehicles).Returns(allVehicles);
    tracking.Setup(t => t.EnrichRoutesAsync(It.IsAny<List<GoogleRoute>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync([routeA, routeB]);
    tracking.Setup(t => t.RefreshVehiclesAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new TransitTrackingResult { Vehicles = allVehicles, StatusMessage = "Live" });

    var location = new Mock<ILocationService>();
    location.Setup(l => l.LastKnownLocation).Returns(new Location(3.139, 101.6869));

    using var session = CreateSession(tracking: tracking, location: location);

    await session.EnrichTransitRoutesAsync([routeA.OriginalRoute, routeB.OriginalRoute]);
    Assert.Equal(3, session.ActiveRouteId);
    Assert.False(session.IsRouteVehicleFilterActive);
    Assert.Equal(4, session.GetDisplayedVehicles().Count);

    // Selecting the already-highlighted first route still activates filtering.
    Assert.True(session.SetActiveRoute(3, announceRoute: true, filterVehicles: true));
    Assert.True(session.IsRouteVehicleFilterActive);
    Assert.Equal("Showing Route 4", session.LocationStatus);
    Assert.Equal(["bus-200", "ktmb-pk", "erl-37"], session.GetDisplayedVehicles().Select(v => v.VehicleId ?? "").ToArray());

    // Periodic refresh preserves the active route filter.
    var refreshed = await session.RefreshVehiclesAsync(force: false);
    Assert.NotNull(refreshed);
    Assert.Equal(["bus-200", "ktmb-pk", "erl-37"], refreshed.Vehicles.Select(v => v.VehicleId ?? "").ToArray());

    // Switching to another route updates the filtered vehicles.
    Assert.True(session.SetActiveRoute(1));
    Assert.Equal(["bus-400"], session.GetDisplayedVehicles().Select(v => v.VehicleId ?? "").ToArray());

    // Clearing routes disables filtering and restores all cached vehicles.
    session.ClearRoutes();
    Assert.False(session.IsRouteVehicleFilterActive);
    Assert.Equal(4, session.GetDisplayedVehicles().Count);
  }

  [Fact]
  public async Task TryBeginRouteCalculation_DeactivatesActiveVehicleFilter()
  {
    var allVehicles = new List<BusLocation>
    {
      new() { VehicleId = "bus-200", RouteId = "200" },
      new() { VehicleId = "bus-400", RouteId = "400" }
    };
    var route = new EnrichedRoute
    {
      OriginalRoute = new GoogleRoute
      {
        RouteId = 0,
        Segments = [new RouteSegment { TravelMode = "TRANSIT", LineShortName = "200" }]
      }
    };

    var tracking = new Mock<ITransitTrackingService>();
    tracking.Setup(t => t.CachedVehicles).Returns(allVehicles);
    tracking.Setup(t => t.EnrichRoutesAsync(It.IsAny<List<GoogleRoute>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync([route]);

    using var session = CreateSession(tracking: tracking);
    await session.EnrichTransitRoutesAsync([route.OriginalRoute]);
    session.SetActiveRoute(0, announceRoute: true, filterVehicles: true);
    Assert.True(session.IsRouteVehicleFilterActive);
    Assert.Single(session.GetDisplayedVehicles());

    Assert.True(session.TryBeginRouteCalculation("KLCC", "KLIA"));
    Assert.False(session.IsRouteVehicleFilterActive);
    Assert.Equal(-1, session.ActiveRouteId);
    Assert.Equal(2, session.GetDisplayedVehicles().Count);
  }

  [Fact]
  public async Task SelectVehicleRouteAsync_ResolvesRouteByIdOrName_AndUpdatesStaticSelection()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([
        new StaticRoute("U4200", "420"),
        new StaticRoute("30000019", "401")
      ]);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "U4200", It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"U4200"}""");
    staticData.Setup(s => s.GetMapAsync(StaticSource, "30000019", It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"30000019"}""");

    var noStaticProvider = new TransportProvider("Rapid Kuantan", "https://feed.test/kuantan", 3.8, 103.3, 40, "Bus");
    var providers = new[]
    {
      noStaticProvider,
      new TransportProvider("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30, "Bus", StaticSource)
    };

    using var session = CreateSession(staticData: staticData, providers: providers);

    // Provider without static data returns null without altering selection.
    Assert.False(session.HasStaticDataForProvider("Rapid Kuantan"));
    Assert.Null(await session.SelectVehicleRouteAsync("Rapid Kuantan", "K100"));
    Assert.Equal(-1, session.SelectedStaticProviderIndex);

    // Direct route_id match loads the full route GeoJSON and syncs static panel state.
    Assert.True(session.HasStaticDataForProvider("Rapid KL"));
    var geoJson = await session.SelectVehicleRouteAsync("Rapid KL", "U4200", "420");
    Assert.Equal("""{"route":"U4200"}""", geoJson);
    Assert.Equal(1, session.SelectedStaticProviderIndex);
    Assert.Equal("U4200", session.SelectedStaticRouteId);
    Assert.Equal(2, session.StaticRoutes.Count);
    Assert.Equal("Select a stop on the map for departures.", session.StaticStatus);

    // Feeds sending route_short_name in routeId (e.g. "401") resolve to the static route_id ("30000019").
    var shortNameGeoJson = await session.SelectVehicleRouteAsync("Rapid KL", "401");
    Assert.Equal("""{"route":"30000019"}""", shortNameGeoJson);
    Assert.Equal("30000019", session.SelectedStaticRouteId);
    staticData.Verify(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task SelectVehicleRouteAsync_IgnoresStaleCompletionAfterNewerSelection()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    var firstGate = new TaskCompletionSource<string>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("R1", "Route 1"), new StaticRoute("R2", "Route 2")]);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "R1", It.IsAny<CancellationToken>()))
      .Returns(() => firstGate.Task);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "R2", It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"R2"}""");

    using var session = CreateSession(staticData: staticData);

    var firstTask = session.SelectVehicleRouteAsync("Rapid KL", "R1");
    var secondGeoJson = await session.SelectVehicleRouteAsync("Rapid KL", "R2");
    firstGate.TrySetResult("""{"route":"R1"}""");

    Assert.Equal("""{"route":"R2"}""", secondGeoJson);
    Assert.Null(await firstTask);
    Assert.Equal("R2", session.SelectedStaticRouteId);
  }

  [Fact]
  public async Task SelectStaticRouteAsync_FiltersDisplayedAndRefreshedVehiclesUntilCleared()
  {
    var allVehicles = new List<BusLocation>
    {
      new() { VehicleId = "kl-401", ProviderName = "Rapid KL", RouteId = "30000019", RouteShortName = "401", TransportType = "Bus" },
      new() { VehicleId = "kl-420", ProviderName = "Rapid KL", RouteId = "U4200", RouteShortName = "420", TransportType = "Bus" },
      new() { VehicleId = "penang-401", ProviderName = "Rapid Penang", RouteId = "401", TransportType = "Bus" }
    };

    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("30000019", "401"), new StaticRoute("U4200", "420")]);
    staticData.Setup(s => s.GetMapAsync(StaticSource, It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"type":"FeatureCollection","features":[]}""");

    var tracking = new Mock<ITransitTrackingService>();
    tracking.Setup(t => t.CachedVehicles).Returns(allVehicles);
    tracking.Setup(t => t.RefreshVehiclesAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new TransitTrackingResult { Vehicles = allVehicles, StatusMessage = "Live" });

    var location = new Mock<ILocationService>();
    location.Setup(l => l.LastKnownLocation).Returns(new Location(3.139, 101.6869));

    using var session = CreateSession(tracking: tracking, staticData: staticData, location: location);

    await session.SelectStaticProviderAsync(0);
    Assert.False(session.IsStaticRouteVehicleFilterActive);
    Assert.Equal(3, session.GetDisplayedVehicles().Count);

    await session.SelectStaticRouteAsync("30000019");
    Assert.True(session.IsStaticRouteVehicleFilterActive);
    Assert.Equal(["kl-401"], session.GetDisplayedVehicles().Select(v => v.VehicleId ?? "").ToArray());

    // Periodic refresh preserves the active static route filter.
    var refreshed = await session.RefreshVehiclesAsync(force: false);
    Assert.NotNull(refreshed);
    Assert.Equal(["kl-401"], refreshed.Vehicles.Select(v => v.VehicleId ?? "").ToArray());

    // Selecting "All routes" restores all vehicles.
    await session.SelectStaticRouteAsync(string.Empty);
    Assert.False(session.IsStaticRouteVehicleFilterActive);
    Assert.Equal(3, session.GetDisplayedVehicles().Count);

    // Selecting a vehicle on the map also activates static route filtering, and changing provider clears it.
    await session.SelectVehicleRouteAsync("Rapid KL", "U4200", "420");
    Assert.True(session.IsStaticRouteVehicleFilterActive);
    Assert.Equal(["kl-420"], session.GetDisplayedVehicles().Select(v => v.VehicleId ?? "").ToArray());

    await session.SelectStaticProviderAsync(-1);
    Assert.False(session.IsStaticRouteVehicleFilterActive);
    Assert.Equal(3, session.GetDisplayedVehicles().Count);
  }

  [Fact]
  public async Task SelectStaticRouteAsync_SetsUnavailableErrorStatus_WhenMapThrowsOrReturnsEmpty()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("R1", "Route 1")]);
    staticData.SetupSequence(s => s.GetMapAsync(StaticSource, "R1", It.IsAny<CancellationToken>()))
      .ThrowsAsync(new HttpRequestException("503"))
      .ReturnsAsync((string)null!)
      .ReturnsAsync("   ")
      .ReturnsAsync("""{"type":"FeatureCollection","features":[]}""");

    using var session = CreateSession(staticData: staticData);

    await session.SelectStaticProviderAsync(0);
    Assert.False(session.IsStaticErrorStatus);

    Assert.Null(await session.SelectStaticRouteAsync("R1"));
    Assert.Equal("Static route map unavailable.", session.StaticStatus);
    Assert.True(session.IsStaticErrorStatus);

    Assert.Null(await session.SelectStaticRouteAsync("R1"));
    Assert.Equal("Static route map unavailable.", session.StaticStatus);
    Assert.True(session.IsStaticErrorStatus);

    Assert.Null(await session.SelectStaticRouteAsync("R1"));
    Assert.Equal("Static route map unavailable.", session.StaticStatus);
    Assert.True(session.IsStaticErrorStatus);

    Assert.NotNull(await session.SelectStaticRouteAsync("R1"));
    Assert.Equal("Select a stop on the map for departures.", session.StaticStatus);
    Assert.False(session.IsStaticErrorStatus);
  }

  [Fact]
  public async Task ClearVehicleRouteSelection_ClearsRouteFilterAndIgnoresInFlightSelection()
  {
    var allVehicles = new List<BusLocation>
    {
      new() { VehicleId = "kl-401", ProviderName = "Rapid KL", RouteId = "30000019", RouteShortName = "401", TransportType = "Bus" },
      new() { VehicleId = "kl-420", ProviderName = "Rapid KL", RouteId = "U4200", RouteShortName = "420", TransportType = "Bus" }
    };

    var mapGate = new TaskCompletionSource<string>();
    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("30000019", "401"), new StaticRoute("U4200", "420")]);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "U4200", It.IsAny<CancellationToken>()))
      .Returns(() => mapGate.Task);

    var tracking = new Mock<ITransitTrackingService>();
    tracking.Setup(t => t.CachedVehicles).Returns(allVehicles);

    using var session = CreateSession(tracking: tracking, staticData: staticData);

    var selectTask = session.SelectVehicleRouteAsync("Rapid KL", "U4200", "420");
    Assert.True(session.IsStaticRouteVehicleFilterActive);
    Assert.Single(session.GetDisplayedVehicles());

    // Deselecting before the route map finishes loading cancels the in-flight map and restores all vehicles.
    Assert.True(session.ClearVehicleRouteSelection("Rapid KL"));
    Assert.False(session.IsStaticRouteVehicleFilterActive);
    Assert.Equal(string.Empty, session.SelectedStaticRouteId);
    Assert.Equal("Select a route to view its map.", session.StaticStatus);
    Assert.Equal(2, session.GetDisplayedVehicles().Count);

    mapGate.TrySetResult("""{"route":"U4200"}""");
    Assert.Null(await selectTask);
  }

  [Fact]
  public async Task ClearAllRoutes_ResetsStaticAndPlannedRoutesToDefault()
  {
    var allVehicles = new List<BusLocation>
    {
      new() { VehicleId = "kl-401", ProviderName = "Rapid KL", RouteId = "30000019", RouteShortName = "401", TransportType = "Bus" },
      new() { VehicleId = "kl-420", ProviderName = "Rapid KL", RouteId = "U4200", RouteShortName = "420", TransportType = "Bus" }
    };

    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("30000019", "401"), new StaticRoute("U4200", "420")]);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "U4200", It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"U4200"}""");
    staticData.Setup(s => s.GetDeparturesAsync(StaticSource, "U4200", "stop-1", It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticDeparture("U4200", "KLCC", DateTimeOffset.UtcNow.AddMinutes(5))]);

    var route = new EnrichedRoute
    {
      OriginalRoute = new GoogleRoute
      {
        RouteId = 2,
        Segments = [new RouteSegment { TravelMode = "TRANSIT", LineShortName = "401" }]
      }
    };

    var tracking = new Mock<ITransitTrackingService>();
    tracking.Setup(t => t.CachedVehicles).Returns(allVehicles);
    tracking.Setup(t => t.EnrichRoutesAsync(It.IsAny<List<GoogleRoute>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync([route]);

    using var session = CreateSession(tracking: tracking, staticData: staticData);

    await session.EnrichTransitRoutesAsync([route.OriginalRoute]);
    await session.SelectStaticProviderAsync(0);
    await session.SelectStaticRouteAsync("U4200");
    await session.LoadStaticDeparturesAsync("stop-1", "Stop 1");

    Assert.Equal(0, session.SelectedStaticProviderIndex);
    Assert.Equal("U4200", session.SelectedStaticRouteId);
    Assert.NotEmpty(session.StaticRoutes);
    Assert.NotEmpty(session.StaticDepartures);
    Assert.True(session.IsStaticRouteVehicleFilterActive);
    Assert.Single(session.GetDisplayedVehicles());

    session.ClearAllRoutes();

    Assert.Equal(-1, session.SelectedStaticProviderIndex);
    Assert.Equal(string.Empty, session.SelectedStaticRouteId);
    Assert.Empty(session.StaticRoutes);
    Assert.Empty(session.StaticDepartures);
    Assert.Equal(string.Empty, session.SelectedStaticStopId);
    Assert.Equal(string.Empty, session.StaticStopName);
    Assert.Equal(string.Empty, session.StaticStatus);
    Assert.False(session.IsStaticErrorStatus);
    Assert.False(session.IsStaticRouteVehicleFilterActive);
    Assert.Empty(session.RouteOptions);
    Assert.Equal(-1, session.ActiveRouteId);
    Assert.False(session.IsRouteVehicleFilterActive);
    Assert.Equal("Route cleared.", session.LocationStatus);
    Assert.Equal(2, session.GetDisplayedVehicles().Count);
  }

  [Fact]
  public void ClearAllRoutes_WithoutAnnounce_PreservesLocationStatus()
  {
    using var session = CreateSession();
    session.ReportLiveLocation();

    session.ClearAllRoutes(announce: false);

    Assert.Equal("Live", session.LocationStatus);
    Assert.False(session.IsErrorStatus);
    Assert.Equal(-1, session.SelectedStaticProviderIndex);
    Assert.Empty(session.RouteOptions);
  }
}
