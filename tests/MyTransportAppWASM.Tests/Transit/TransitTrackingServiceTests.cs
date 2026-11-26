using Moq;

namespace MyTransportAppWASM.Tests;

public class TransitTrackingServiceTests
{
  private static readonly TransportProvider RapidKl =
      new("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30.0, "Bus");

  [Fact]
  public async Task RefreshVehiclesAsync_LocationFarAway_ReturnsNoNearbyFeeds()
  {
    var (service, gtfs, _) = CreateService([RapidKl]);

    // Location in London (far from KL)
    var result = await service.RefreshVehiclesAsync(51.5074, -0.1278);

    Assert.False(result.HasNearbyFeeds);
    Assert.Equal(0, result.NearbyProviderCount);
    Assert.Empty(result.Vehicles);
    Assert.Contains("No live vehicle feeds nearby", result.StatusMessage);
    gtfs.Verify(g => g.GetBusPositionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task RefreshVehiclesAsync_NearbyProvider_FetchesVehiclesAndSetsTransportType()
  {
    var (service, gtfs, _) = CreateService([RapidKl]);
    SetupVehicles(gtfs, RapidKl, new BusLocation { VehicleId = "BUS-99", RouteId = "U60" });

    var result = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);

    Assert.True(result.HasNearbyFeeds);
    Assert.Equal(1, result.NearbyProviderCount);
    Assert.Single(result.Vehicles);
    Assert.Equal("Bus", result.Vehicles[0].TransportType);
    Assert.Equal("BUS-99", result.Vehicles[0].VehicleId);
    Assert.NotNull(result.LastUpdated);
    Assert.Contains("Buses updated:", result.StatusMessage);
    Assert.Single(service.CachedVehicles);
  }

  [Fact]
  public async Task EnrichRoutesAsync_DecoratesWithFortuneWhenBaziFlowAvailable()
  {
    var (service, _, bazi) = CreateService();
    SetupLuckyHours(bazi, [MalaysiaTime.Now.Hour]);

    var routes = await service.EnrichRoutesAsync([new GoogleRoute { RouteId = 1, DurationSeconds = 600 }]);

    Assert.Single(routes);
    Assert.True(routes[0].IsAuspicious);
  }

  [Fact]
  public async Task Reset_ClearsCachedVehicles()
  {
    var (service, gtfs, _) = CreateService([RapidKl]);
    SetupVehicles(gtfs, RapidKl, new BusLocation { VehicleId = "BUS-1" });
    await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);

    Assert.Single(service.CachedVehicles);

    service.Reset();
    Assert.Empty(service.CachedVehicles);
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task EnrichRoutesAsync_WithoutMatchingLuckyHour_DoesNotMarkRoutesAuspicious(bool useNullLuckyHours)
  {
    var (service, _, bazi) = CreateService();
    // hour guaranteed not to match current hour
    SetupLuckyHours(bazi, useNullLuckyHours ? null : [(MalaysiaTime.Now.Hour + 12) % 24]);

    var routes = await service.EnrichRoutesAsync([
      new GoogleRoute { RouteId = 1, DurationSeconds = 600 },
      new GoogleRoute { RouteId = 2, DurationSeconds = 700 }
    ]);

    Assert.Equal(2, routes.Count);
    Assert.False(routes[0].IsAuspicious);
    Assert.False(routes[1].IsAuspicious);
  }

  [Fact]
  public async Task RefreshVehiclesAsync_TagsVehiclesWithProviderName_AcrossMultipleProviders()
  {
    var providerA = new TransportProvider("Rapid Bus KL", "https://feed.test/kl", 3.139, 101.6869, 30.0, "Bus");
    var providerB = new TransportProvider("Rapid Bus MRT Feeder", "https://feed.test/mrt", 3.139, 101.6869, 30.0, "Bus");
    var (service, gtfs, _) = CreateService([providerA, providerB]);
    SetupVehicles(gtfs, providerA, new BusLocation { VehicleId = "101", RouteId = "U60" });
    SetupVehicles(gtfs, providerB, new BusLocation { VehicleId = "101", RouteId = "T400" });

    var result = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);

    Assert.Equal(2, result.Vehicles.Count);
    Assert.Contains(result.Vehicles, v => v.ProviderName == "Rapid Bus KL" && v.VehicleId == "101");
    Assert.Contains(result.Vehicles, v => v.ProviderName == "Rapid Bus MRT Feeder" && v.VehicleId == "101");
  }

  [Fact]
  public void Dispose_DisposesCleanlyAndImplementsIDisposable()
  {
    var (service, _, _) = CreateService();
    Assert.IsAssignableFrom<IDisposable>(service);

    service.Dispose();
    service.Dispose(); // idempotent
  }

  [Fact]
  public async Task RefreshVehiclesAsync_WithBoundedMemoryCache_StoresVehiclesWithSizeLimit()
  {
    using var cache = CreateBoundedCache();
    var (service, gtfs, _) = CreateService([RapidKl], cache);
    SetupVehicles(gtfs, RapidKl, new BusLocation { VehicleId = "BUS-SIZE", RouteId = "U60" });

    var result = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);

    Assert.Single(result.Vehicles);
    Assert.Single(service.CachedVehicles);
    Assert.True(cache.TryGetValue("transit_cached_vehicles", out List<BusLocation>? cached));
    Assert.NotNull(cached);
    Assert.Single(cached);
  }

  [Fact]
  public async Task EnrichRoutesAsync_WithBoundedMemoryCache_CachesFortuneWithSizeLimit()
  {
    using var cache = CreateBoundedCache();
    var (service, _, bazi) = CreateService(cache: cache);
    var googleRoute = new GoogleRoute { RouteId = 1, DurationSeconds = 600 };
    SetupLuckyHours(bazi, [MalaysiaTime.Now.Hour]);

    var routes = await service.EnrichRoutesAsync([googleRoute]);

    Assert.Single(routes);
    Assert.True(routes[0].IsAuspicious);
    bazi.Verify(b => b.GetDateFortuneAsync(It.IsAny<string>()), Times.Once);

    // Second call should hit the cache without calling BaziFlowService again
    var routes2 = await service.EnrichRoutesAsync([googleRoute]);
    Assert.Single(routes2);
    bazi.Verify(b => b.GetDateFortuneAsync(It.IsAny<string>()), Times.Once);
  }

  [Fact]
  public async Task RefreshVehiclesAsync_WhenMovingFromNearbyToFar_ClearsVehiclesFromMemoryCache()
  {
    using var cache = CreateBoundedCache();
    var (service, gtfs, _) = CreateService([RapidKl], cache);
    SetupVehicles(gtfs, RapidKl, new BusLocation { VehicleId = "BUS-NEAR", RouteId = "U60" });

    // 1. Refresh near provider -> vehicles cached
    var nearResult = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);
    Assert.Single(nearResult.Vehicles);
    Assert.Single(service.CachedVehicles);
    Assert.True(cache.TryGetValue("transit_cached_vehicles", out List<BusLocation>? cached));
    Assert.NotNull(cached);

    // 2. Move far away (e.g., London) -> no nearby feeds
    var farResult = await service.RefreshVehiclesAsync(51.5074, -0.1278);
    Assert.False(farResult.HasNearbyFeeds);
    Assert.Empty(farResult.Vehicles);
    // Crucial: CachedVehicles must be empty, not returning stale cached vehicles
    Assert.Empty(service.CachedVehicles);
    Assert.False(cache.TryGetValue<object>("transit_cached_vehicles", out _));
  }

  [Fact]
  public async Task Reset_WithBoundedMemoryCache_ClearsBothVehiclesAndFortuneCache()
  {
    using var cache = CreateBoundedCache();
    var (service, gtfs, bazi) = CreateService([RapidKl], cache);
    SetupVehicles(gtfs, RapidKl, new BusLocation { VehicleId = "BUS-RESET" });
    SetupLuckyHours(bazi, [MalaysiaTime.Now.Hour]);

    await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);
    await service.EnrichRoutesAsync([new GoogleRoute()]);

    Assert.Single(service.CachedVehicles);
    var todayKey = MalaysiaTime.Now.ToString("yyyy-MM-dd");
    Assert.True(cache.TryGetValue<object>("transit_cached_vehicles", out _));
    Assert.True(cache.TryGetValue<object>($"transit_fortune_{todayKey}", out _));

    service.Reset();

    Assert.Empty(service.CachedVehicles);
    Assert.False(cache.TryGetValue<object>("transit_cached_vehicles", out _));
    Assert.False(cache.TryGetValue<object>($"transit_fortune_{todayKey}", out _));
  }

  [Fact]
  public async Task RefreshVehiclesAsync_MapsRouteShortNameFromStaticRoutes_AndCachesLookup()
  {
    var gtfs = new Mock<IGtfsService>();
    var staticData = new Mock<ITransportStaticDataService>();
    var bazi = new Mock<IBaziFlowService>();

    var provider = new TransportProvider(
      "Rapid Bus KL",
      "https://feed.test/kl",
      3.139,
      101.6869,
      30.0,
      "Bus",
      new TransportStaticData("https://static.test/rapid-bus-kl", "/data/routes", "/map", "/departures"));

    SetupVehicles(
      gtfs,
      provider,
      new BusLocation { VehicleId = "BUS-420", RouteId = "U4200", Lat = 3.140f, Lng = 101.6870f },
      new BusLocation { VehicleId = "BUS-RAW", RouteId = "401", Lat = 3.141f, Lng = 101.6871f });

    staticData.Setup(s => s.GetRoutesAsync(provider.StaticData!, It.IsAny<CancellationToken>()))
        .ReturnsAsync(
        [
          new StaticRoute("U4200", "420"),
          new StaticRoute("30000019", "401")
        ]);

    var service = new TransitTrackingService(
      gtfs.Object,
      bazi.Object,
      [provider],
      staticDataService: staticData.Object);

    var first = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);
    var second = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);

    Assert.Equal("420", first.Vehicles[0].RouteShortName);
    Assert.Null(first.Vehicles[1].RouteShortName);
    Assert.Equal("420", second.Vehicles[0].RouteShortName);
    staticData.Verify(s => s.GetRoutesAsync(provider.StaticData!, It.IsAny<CancellationToken>()), Times.Once);

    var enriched = await service.EnrichRoutesAsync(
    [
      new GoogleRoute
      {
        RouteId = 0,
        DurationSeconds = 600,
        TransitSteps = [new TransitStep { LineShortName = "420", DepartureLat = 3.139, DepartureLng = 101.6869 }]
      }
    ]);

    var route = Assert.Single(enriched);
    Assert.True(route.HasLiveBus);
    Assert.Equal("420", Assert.Single(route.NearestBuses).RouteId);

    service.Reset();
    await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);
    staticData.Verify(s => s.GetRoutesAsync(provider.StaticData!, It.IsAny<CancellationToken>()), Times.Exactly(2));
  }

  [Fact]
  public async Task RefreshVehiclesAsync_ContinuesWhenStaticRouteLookupFails()
  {
    var gtfs = new Mock<IGtfsService>();
    var bazi = new Mock<IBaziFlowService>();
    var staticData = new Mock<ITransportStaticDataService>();

    var provider = new TransportProvider(
      "Rapid Bus KL",
      "https://feed.test/kl",
      3.139,
      101.6869,
      30.0,
      "Bus",
      new TransportStaticData("https://static.test/rapid-bus-kl", "/data/routes", "/map", "/departures"));

    SetupVehicles(gtfs, provider, new BusLocation { VehicleId = "BUS-420", RouteId = "U4200" });

    staticData.Setup(s => s.GetRoutesAsync(provider.StaticData!, It.IsAny<CancellationToken>()))
        .ThrowsAsync(new HttpRequestException("Static routes unavailable"));

    var service = new TransitTrackingService(
      gtfs.Object,
      bazi.Object,
      [provider],
      staticDataService: staticData.Object);

    var result = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);

    var vehicle = Assert.Single(result.Vehicles);
    Assert.Equal("U4200", vehicle.RouteId);
    Assert.Null(vehicle.RouteShortName);
  }

  [Fact]
  public async Task EnrichRoutesAsync_RetriesFortuneFetch_WhenPreviousAttemptReturnedNullWithoutCache()
  {
    var gtfs = new Mock<IGtfsService>();
    var bazi = new Mock<IBaziFlowService>();
    bazi.Setup(b => b.HasApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
    bazi.SetupSequence(b => b.GetDateFortuneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((FortuneData?)null)
        .ReturnsAsync(new FortuneData { LuckyHours = [MalaysiaTime.Now.Hour] });

    var service = new TransitTrackingService(gtfs.Object, bazi.Object, []);

    var first = await service.EnrichRoutesAsync([new GoogleRoute()]);
    Assert.False(Assert.Single(first).IsAuspicious);

    var second = await service.EnrichRoutesAsync([new GoogleRoute()]);
    Assert.True(Assert.Single(second).IsAuspicious);
    bazi.Verify(b => b.GetDateFortuneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
  }

  [Fact]
  public async Task RefreshVehiclesAsync_NearbyFrequencyProvider_EstimatesRailPositionsAndHandlesMidnightRollover()
  {
    var gtfs = new Mock<IGtfsService>();
    var bazi = new Mock<IBaziFlowService>();
    var staticData = new Mock<ITransportStaticDataService>();

    var railSource = new TransportStaticData(
      "https://worker.example/rapid-rail-kl",
      "/data/routes",
      "/map",
      "/departures",
      "/data/frequencies");
    var railProvider = new TransportProvider("Rapid Rail KL", "", 3.139, 101.6869, 60.0, "Rail", railSource);

    const string fridayMapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": { "type": "LineString", "coordinates": [[101.68, 3.13], [101.70, 3.15]] },
            "properties": { "kind": "route", "route_id": "KJ", "shape_id": "shp_KJ_0", "trip_ids": ["KJL_MonFri_0"], "route_short_name": "KJL" }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.68, 3.13] },
            "properties": { "kind": "stop", "route_id": "KJ", "stop_id": "KJ1", "trip_ids": ["KJL_MonFri_0"], "arrival_times": "06:00:00" }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.15] },
            "properties": { "kind": "stop", "route_id": "KJ", "stop_id": "KJ2", "trip_ids": ["KJL_MonFri_0"], "arrival_times": "06:20:00" }
          }
        ]
      }
      """;

    const string saturdayMapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": { "type": "LineString", "coordinates": [[101.68, 3.13], [101.70, 3.15]] },
            "properties": { "kind": "route", "route_id": "KJ", "shape_id": "shp_KJ_0", "trip_ids": ["KJL_Sat_0"], "route_short_name": "KJL" }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.68, 3.13] },
            "properties": { "kind": "stop", "route_id": "KJ", "stop_id": "KJ1", "trip_ids": ["KJL_Sat_0"], "arrival_times": "06:00:00" }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.15] },
            "properties": { "kind": "stop", "route_id": "KJ", "stop_id": "KJ2", "trip_ids": ["KJL_Sat_0"], "arrival_times": "06:20:00" }
          }
        ]
      }
      """;

    List<StaticFrequency> frequencies =
    [
      new("KJL_MonFri_0", "23:40:00", "24:00:00", 600),
      new("KJL_Sat_0", "6:00:00", "23:30:00", 600)
    ];

    staticData.Setup(s => s.GetFrequenciesAsync(railSource, It.IsAny<CancellationToken>()))
      .ReturnsAsync(frequencies);
    staticData.Setup(s => s.GetMapAsync(railSource, "", "2026-06-05", It.IsAny<CancellationToken>()))
      .ReturnsAsync(fridayMapGeoJson);
    staticData.Setup(s => s.GetMapAsync(railSource, "", "2026-06-06", It.IsAny<CancellationToken>()))
      .ReturnsAsync(saturdayMapGeoJson);

    // Friday 2026-06-05 at 23:52:00 MYT (active departure: 23:50 and 23:40 -> 2 trains)
    var currentTime = new DateTimeOffset(2026, 6, 5, 23, 52, 0, MalaysiaTime.Offset).ToUniversalTime();
    using var cache = CreateBoundedCache();
    var service = new TransitTrackingService(
      gtfs.Object,
      bazi.Object,
      [railProvider],
      cache: cache,
      staticDataService: staticData.Object,
      utcNowProvider: () => currentTime);

    var first = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);
    Assert.Equal(2, first.Vehicles.Count);
    var train2350 = Assert.Single(first.Vehicles, v => v.VehicleId == "KJL-0-2350");
    Assert.True(train2350.IsEstimated);
    Assert.Equal("KJL", train2350.RouteShortName);
    Assert.Equal("LRT", train2350.TransportType);
    Assert.Contains("incl. estimated rail", first.StatusMessage);

    // Advance 2 minutes (still Friday 23:54) -> moves forward without refetching /data/frequencies or /map
    currentTime = currentTime.AddMinutes(2);
    var second = await service.RefreshVehiclesAsync(3.140, 101.6870, force: false);
    var moved2350 = Assert.Single(second.Vehicles, v => v.VehicleId == "KJL-0-2350");
    Assert.True(moved2350.Lat > train2350.Lat);
    staticData.Verify(s => s.GetFrequenciesAsync(railSource, It.IsAny<CancellationToken>()), Times.Once);
    staticData.Verify(s => s.GetMapAsync(railSource, "", "2026-06-05", It.IsAny<CancellationToken>()), Times.Once);

    // Estimated rail trains participate in live route enrichment while active
    var enriched = await service.EnrichRoutesAsync(
    [
      new GoogleRoute
      {
        RouteId = 0,
        DurationSeconds = 600,
        TransitSteps = [new TransitStep { LineShortName = "KJL", DepartureLat = 3.15, DepartureLng = 101.70 }]
      }
    ]);
    Assert.True(Assert.Single(enriched).HasLiveBus);

    // Cross midnight into Saturday 2026-06-06 00:05:00 MYT -> switches to Saturday map and clears estimated rail after 12am
    currentTime = new DateTimeOffset(2026, 6, 6, 0, 5, 0, MalaysiaTime.Offset).ToUniversalTime();
    var afterMidnight = await service.RefreshVehiclesAsync(3.140, 101.6870, force: false);
    Assert.Empty(afterMidnight.Vehicles);
    Assert.True(afterMidnight.VehiclesChanged);
    staticData.Verify(s => s.GetMapAsync(railSource, "", "2026-06-06", It.IsAny<CancellationToken>()), Times.Once);
    staticData.Verify(s => s.GetMapAsync(railSource, "", "2026-06-05", It.IsAny<CancellationToken>()), Times.Once);

    // Reset clears cached frequencies and day maps
    service.Reset();
    await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);
    staticData.Verify(s => s.GetFrequenciesAsync(railSource, It.IsAny<CancellationToken>()), Times.Exactly(2));
    staticData.Verify(s => s.GetMapAsync(railSource, "", "2026-06-06", It.IsAny<CancellationToken>()), Times.Exactly(2));
  }

  [Fact]
  public async Task RefreshVehiclesAsync_ContinuesServingLiveBusesWhenFrequenciesFetchFails()
  {
    var gtfs = new Mock<IGtfsService>();
    var bazi = new Mock<IBaziFlowService>();
    var staticData = new Mock<ITransportStaticDataService>();

    var railSource = new TransportStaticData(
      "https://worker.example/rapid-rail-kl", "/data/routes", "/map", "/departures", "/data/frequencies");
    var railProvider = new TransportProvider("Rapid Rail KL", "", 3.139, 101.6869, 60.0, "Rail", railSource);
    SetupVehicles(gtfs, RapidKl, new BusLocation { VehicleId = "BUS-1", RouteId = "U60" });

    staticData.Setup(s => s.GetFrequenciesAsync(railSource, It.IsAny<CancellationToken>()))
      .ThrowsAsync(new HttpRequestException("Frequencies unavailable"));

    var service = new TransitTrackingService(
      gtfs.Object,
      bazi.Object,
      [RapidKl, railProvider],
      staticDataService: staticData.Object);

    var result = await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);
    var bus = Assert.Single(result.Vehicles);
    Assert.Equal("BUS-1", bus.VehicleId);
    Assert.False(bus.IsEstimated);
  }

  private static AppMemoryCache CreateBoundedCache() =>
      new(sizeLimit: 10);

  private static (TransitTrackingService Service, Mock<IGtfsService> Gtfs, Mock<IBaziFlowService> Bazi) CreateService(
      IReadOnlyList<TransportProvider>? providers = null,
      IAppCache? cache = null)
  {
    var gtfs = new Mock<IGtfsService>();
    var bazi = new Mock<IBaziFlowService>();
    return (new TransitTrackingService(gtfs.Object, bazi.Object, providers ?? [], cache: cache), gtfs, bazi);
  }

  private static void SetupVehicles(Mock<IGtfsService> gtfs, TransportProvider provider, params BusLocation[] vehicles) =>
      gtfs.Setup(g => g.GetBusPositionsAsync(provider.Endpoint, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new GtfsFetchResult(GtfsFetchStatus.Updated, [.. vehicles]));

  private static void SetupLuckyHours(Mock<IBaziFlowService> bazi, List<int>? luckyHours)
  {
    bazi.Setup(b => b.HasApiKeyAsync()).ReturnsAsync(true);
    bazi.Setup(b => b.GetDateFortuneAsync(It.IsAny<string>()))
        .ReturnsAsync(new FortuneData { LuckyHours = luckyHours! });
  }
}
