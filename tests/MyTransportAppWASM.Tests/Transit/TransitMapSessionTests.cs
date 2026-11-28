using Microsoft.Extensions.Options;
using Moq;

namespace MyTransportAppWASM.Tests;

public class TransitMapSessionTests
{
  private static readonly TransportStaticData StaticSource =
    new("https://worker.example/rapid", "/routes", "/map");

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
    staticData.Verify(s => s.GetMapAsync(StaticSource, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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
    Assert.Equal("R2", route.RouteId);
    staticData.Verify(s => s.GetMapAsync(StaticSource, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task SelectStaticProvider_UpdatesSessionState()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("R1", "Route 1")]);

    using var session = CreateSession(staticData: staticData);

    var geoJson = await session.SelectStaticProviderAsync(0);
    Assert.Null(geoJson);
    Assert.Single(session.StaticRoutes);
    Assert.Equal("Select a route to view its map.", session.StaticStatus);
    staticData.Verify(s => s.GetMapAsync(StaticSource, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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
        new StaticRoute("U4200", "420", "Pandan Indah - Lebuh Ampang"),
        new StaticRoute("30000019", "401", "401"),
        new StaticRoute("30000009", null, "T808")
      ]);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "U4200", null, It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"U4200"}""");
    staticData.Setup(s => s.GetMapAsync(StaticSource, "30000019", null, It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"30000019"}""");
    staticData.Setup(s => s.GetMapAsync(StaticSource, "30000009", null, It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"30000009"}""");

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
    Assert.Equal(3, session.StaticRoutes.Count);
    Assert.Equal("Select a stop on the map for details.", session.StaticStatus);

    // Feeds sending route_short_name in routeId (e.g. "401") resolve to the static route_id ("30000019").
    var shortNameGeoJson = await session.SelectVehicleRouteAsync("Rapid KL", "401");
    Assert.Equal("""{"route":"30000019"}""", shortNameGeoJson);
    Assert.Equal("30000019", session.SelectedStaticRouteId);

    // Feeds sending route_long_name in routeId (e.g. MRT feeder "T808") resolve to the static route_id ("30000009").
    var longNameGeoJson = await session.SelectVehicleRouteAsync("Rapid KL", "T808");
    Assert.Equal("""{"route":"30000009"}""", longNameGeoJson);
    Assert.Equal("30000009", session.SelectedStaticRouteId);

    // Unmatched route names must not call /map with route_short_name/route_long_name or unknown values.
    Assert.Null(await session.SelectVehicleRouteAsync("Rapid KL", "Unknown Line"));
    Assert.Equal(string.Empty, session.SelectedStaticRouteId);
    Assert.False(session.IsStaticRouteVehicleFilterActive);
    Assert.True(session.IsStaticErrorStatus);
    staticData.Verify(s => s.GetMapAsync(StaticSource, "Unknown Line", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    staticData.Verify(s => s.GetMapAsync(StaticSource, "401", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    staticData.Verify(s => s.GetMapAsync(StaticSource, "T808", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    staticData.Verify(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task SelectStaticRouteAsync_AlwaysCallsMapWithRouteId_AndRejectsUnresolvedNames()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([
        new StaticRoute("30000019", "401", "401"),
        new StaticRoute("30000009", null, "T808")
      ]);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "30000019", null, It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"30000019"}""");
    staticData.Setup(s => s.GetMapAsync(StaticSource, "30000009", null, It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"30000009"}""");

    using var session = CreateSession(staticData: staticData);
    await session.SelectStaticProviderAsync(0);

    Assert.Equal("""{"route":"30000019"}""", await session.SelectStaticRouteAsync("401"));
    Assert.Equal("30000019", session.SelectedStaticRouteId);

    Assert.Equal("""{"route":"30000009"}""", await session.SelectStaticRouteAsync("T808"));
    Assert.Equal("30000009", session.SelectedStaticRouteId);

    Assert.Null(await session.SelectStaticRouteAsync("Missing Route"));
    Assert.Equal(string.Empty, session.SelectedStaticRouteId);
    Assert.False(session.IsStaticRouteVehicleFilterActive);
    Assert.True(session.IsStaticErrorStatus);
    staticData.Verify(s => s.GetMapAsync(StaticSource, "401", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    staticData.Verify(s => s.GetMapAsync(StaticSource, "T808", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    staticData.Verify(s => s.GetMapAsync(StaticSource, "Missing Route", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task SelectVehicleRouteAsync_IgnoresStaleCompletionAfterNewerSelection()
  {
    var staticData = new Mock<ITransportStaticDataService>();
    var firstGate = new TaskCompletionSource<string>();
    staticData.Setup(s => s.GetRoutesAsync(StaticSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("R1", "Route 1"), new StaticRoute("R2", "Route 2")]);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "R1", null, It.IsAny<CancellationToken>()))
      .Returns(() => firstGate.Task);
    staticData.Setup(s => s.GetMapAsync(StaticSource, "R2", null, It.IsAny<CancellationToken>()))
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
    staticData.Setup(s => s.GetMapAsync(StaticSource, It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
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
    staticData.SetupSequence(s => s.GetMapAsync(StaticSource, "R1", null, It.IsAny<CancellationToken>()))
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
    Assert.Equal("Select a stop on the map for details.", session.StaticStatus);
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
    staticData.Setup(s => s.GetMapAsync(StaticSource, "U4200", null, It.IsAny<CancellationToken>()))
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
    staticData.Setup(s => s.GetMapAsync(StaticSource, "U4200", null, It.IsAny<CancellationToken>()))
      .ReturnsAsync("""{"route":"U4200"}""");

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

    Assert.Equal(0, session.SelectedStaticProviderIndex);
    Assert.Equal("U4200", session.SelectedStaticRouteId);
    Assert.NotEmpty(session.StaticRoutes);
    Assert.True(session.IsStaticRouteVehicleFilterActive);
    Assert.Single(session.GetDisplayedVehicles());

    session.ClearAllRoutes();

    Assert.Equal(-1, session.SelectedStaticProviderIndex);
    Assert.Equal(string.Empty, session.SelectedStaticRouteId);
    Assert.Empty(session.StaticRoutes);
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

  [Fact]
  public async Task SelectStaticRouteAsync_EnrichesMapWithFrequenciesAndServiceDate_WhenProviderHasFrequencyFeed()
  {
    var railSource = new TransportStaticData(
      "https://worker.example/rapid-rail-kl",
      "/data/routes",
      "/map",
      "/data/frequencies");
    var providers = new[]
    {
      new TransportProvider("Rapid Rail KL", "", 3.139, 101.6869, 60, "Rail", railSource)
    };
    const string railMapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.68, 3.13] },
            "properties": {
              "kind": "stop", "route_id": "KJ", "stop_id": "KJ1",
              "trip_ids": ["KJ_0"], "arrival_times": "06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.69, 3.14] },
            "properties": {
              "kind": "stop", "route_id": "KJ", "stop_id": "KJ2",
              "trip_ids": ["KJ_0"], "arrival_times": "06:04:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.15] },
            "properties": {
              "kind": "stop", "route_id": "AG", "stop_id": "AG1",
              "trip_ids": ["AG_0"], "arrival_times": "06:00:00"
            }
          }
        ]
      }
      """;

    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(railSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("KJ", "KJL", "Kelana Jaya Line")]);
    staticData.Setup(s => s.GetMapAsync(railSource, string.Empty, It.Is<string?>(d => !string.IsNullOrWhiteSpace(d)), It.IsAny<CancellationToken>()))
      .ReturnsAsync(railMapGeoJson);
    staticData.Setup(s => s.GetFrequenciesAsync(railSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticFrequency("KJ_0", "06:00:00", "07:00:00", 600)]);

    using var session = CreateSession(staticData: staticData, providers: providers);
    await session.SelectStaticProviderAsync(0);

    var result = await session.SelectStaticRouteAsync("KJ");
    Assert.NotNull(result);
    using var doc = System.Text.Json.JsonDocument.Parse(result);
    var features = doc.RootElement.GetProperty("features");
    Assert.Equal(2, features.GetArrayLength());
    var stop2Props = features[1].GetProperty("properties");
    var arrWindow = stop2Props.GetProperty("frequency_arrivals")[0];
    Assert.Equal(21840, arrWindow.GetProperty("start").GetInt32());
    Assert.Equal(24840, arrWindow.GetProperty("end").GetInt32());
    Assert.Equal(600, arrWindow.GetProperty("headway").GetInt32());
    staticData.Verify(s => s.GetMapAsync(railSource, string.Empty, It.Is<string?>(d => !string.IsNullOrWhiteSpace(d)), It.IsAny<CancellationToken>()), Times.Once);
    staticData.Verify(s => s.GetFrequenciesAsync(railSource, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task SelectStaticRouteAsync_ReturnsFilteredBaseMapGeoJson_WhenFrequenciesRequestFails()
  {
    var railSource = new TransportStaticData(
      "https://worker.example/rapid-rail-kl",
      "/data/routes",
      "/map",
      "/data/frequencies");
    var providers = new[]
    {
      new TransportProvider("Rapid Rail KL", "", 3.139, 101.6869, 60, "Rail", railSource)
    };
    const string railMapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          { "type": "Feature", "geometry": { "type": "Point", "coordinates": [101.68, 3.13] }, "properties": { "kind": "stop", "route_id": "KJ", "stop_id": "KJ1" } },
          { "type": "Feature", "geometry": { "type": "Point", "coordinates": [101.70, 3.15] }, "properties": { "kind": "stop", "route_id": "AG", "stop_id": "AG1" } }
        ]
      }
      """;

    var staticData = new Mock<ITransportStaticDataService>();
    staticData.Setup(s => s.GetRoutesAsync(railSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync([new StaticRoute("KJ", "KJL", "Kelana Jaya Line")]);
    staticData.Setup(s => s.GetMapAsync(railSource, string.Empty, It.Is<string?>(d => !string.IsNullOrWhiteSpace(d)), It.IsAny<CancellationToken>()))
      .ReturnsAsync(railMapGeoJson);
    staticData.Setup(s => s.GetFrequenciesAsync(railSource, It.IsAny<CancellationToken>()))
      .ThrowsAsync(new HttpRequestException("503"));

    using var session = CreateSession(staticData: staticData, providers: providers);
    await session.SelectStaticProviderAsync(0);

    var result = await session.SelectStaticRouteAsync("KJ");
    Assert.NotNull(result);
    using var doc = System.Text.Json.JsonDocument.Parse(result);
    var features = doc.RootElement.GetProperty("features");
    Assert.Equal(1, features.GetArrayLength());
    Assert.Equal("KJ", features[0].GetProperty("properties").GetProperty("route_id").GetString());
    Assert.Equal("Select a stop on the map for details.", session.StaticStatus);
    Assert.False(session.IsStaticErrorStatus);
  }
}
