using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Models.BaziFlow;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class TransitTrackingServiceTests
{
  [Fact]
  public async Task RefreshVehiclesAsync_LocationFarAway_ReturnsNoNearbyFeeds()
  {
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    var providers = new List<TransportProvider>
    {
      new("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30.0, "Bus")
    };

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, providers);

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
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    var provider = new TransportProvider("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30.0, "Bus");
    var vehicle = new BusLocation { VehicleId = "BUS-99", RouteId = "U60" };

    gtfs.Setup(g => g.GetBusPositionsAsync(provider.Endpoint, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GtfsFetchResult(GtfsFetchStatus.Updated, [vehicle]));

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, [provider]);

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
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    var googleRoute = new GoogleRoute { RouteId = 1, DurationSeconds = 600 };
    var enrichedRoute = new EnrichedRoute { OriginalRoute = googleRoute, LiveTotalDurationSeconds = 600 };

    routing.Setup(r => r.EnrichRoutes(It.IsAny<List<GoogleRoute>>(), It.IsAny<List<BusLocation>>()))
           .Returns([enrichedRoute]);

    bazi.Setup(b => b.HasApiKeyAsync()).ReturnsAsync(true);
    bazi.Setup(b => b.GetDateFortuneAsync(It.IsAny<string>()))
        .ReturnsAsync(new FortuneData
        {
          LuckyHours = [MalaysiaTime.Now.Hour]
        });

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, []);

    var routes = await service.EnrichRoutesAsync([googleRoute]);

    Assert.Single(routes);
    Assert.True(routes[0].IsAuspicious);
  }

  [Fact]
  public async Task Reset_ClearsCachedVehicles()
  {
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    var provider = new TransportProvider("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30.0, "Bus");
    gtfs.Setup(g => g.GetBusPositionsAsync(provider.Endpoint, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GtfsFetchResult(GtfsFetchStatus.Updated, [new BusLocation { VehicleId = "BUS-1" }]));

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, [provider]);
    await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);

    Assert.Single(service.CachedVehicles);

    service.Reset();
    Assert.Empty(service.CachedVehicles);
  }

  [Fact]
  public async Task EnrichRoutesAsync_WithNullLuckyHours_DoesNotThrow()
  {
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    var googleRoute = new GoogleRoute { RouteId = 1, DurationSeconds = 600 };
    routing.Setup(r => r.EnrichRoutes(It.IsAny<List<GoogleRoute>>(), It.IsAny<List<BusLocation>>()))
        .Returns([
          new EnrichedRoute { OriginalRoute = googleRoute, LiveTotalDurationSeconds = 600 },
          new EnrichedRoute { OriginalRoute = googleRoute, LiveTotalDurationSeconds = 700 }
        ]);

    bazi.Setup(b => b.HasApiKeyAsync()).ReturnsAsync(true);
    bazi.Setup(b => b.GetDateFortuneAsync(It.IsAny<string>()))
        .ReturnsAsync(new FortuneData
        {
          LuckyHours = null!
        });

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, []);

    var routes = await service.EnrichRoutesAsync([googleRoute]);

    Assert.Equal(2, routes.Count);
    Assert.True(routes[0].IsAuspicious);
    Assert.False(routes[1].IsAuspicious);
  }

  [Fact]
  public async Task EnrichRoutesAsync_WhenNotLuckyHour_AlternatesAuspiciousBadge()
  {
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    var googleRoute = new GoogleRoute { RouteId = 1, DurationSeconds = 600 };
    routing.Setup(r => r.EnrichRoutes(It.IsAny<List<GoogleRoute>>(), It.IsAny<List<BusLocation>>()))
        .Returns([
          new EnrichedRoute { OriginalRoute = googleRoute, LiveTotalDurationSeconds = 600 },
          new EnrichedRoute { OriginalRoute = googleRoute, LiveTotalDurationSeconds = 700 }
        ]);

    bazi.Setup(b => b.HasApiKeyAsync()).ReturnsAsync(true);
    bazi.Setup(b => b.GetDateFortuneAsync(It.IsAny<string>()))
        .ReturnsAsync(new FortuneData
        {
          // hour guaranteed not to match current hour
          LuckyHours = [(MalaysiaTime.Now.Hour + 12) % 24]
        });

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, []);

    var routes = await service.EnrichRoutesAsync([googleRoute]);

    Assert.Equal(2, routes.Count);
    // Alternating demonstration: index 0 is auspicious, index 1 is not
    Assert.True(routes[0].IsAuspicious);
    Assert.False(routes[1].IsAuspicious);
  }

  [Fact]
  public void Dispose_DisposesCleanlyAndImplementsIDisposable()
  {
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, []);
    Assert.IsAssignableFrom<IDisposable>(service);

    service.Dispose();
    service.Dispose(); // idempotent
  }

  [Fact]
  public async Task RefreshVehiclesAsync_WithBoundedMemoryCache_StoresVehiclesWithSizeLimit()
  {
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
      new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 10 });

    var provider = new TransportProvider("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30.0, "Bus");
    var vehicle = new BusLocation { VehicleId = "BUS-SIZE", RouteId = "U60" };
    gtfs.Setup(g => g.GetBusPositionsAsync(provider.Endpoint, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GtfsFetchResult(GtfsFetchStatus.Updated, [vehicle]));

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, [provider], cache: cache);
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
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
      new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 10 });

    var googleRoute = new GoogleRoute { RouteId = 1, DurationSeconds = 600 };
    var enrichedRoute = new EnrichedRoute { OriginalRoute = googleRoute, LiveTotalDurationSeconds = 600 };
    routing.Setup(r => r.EnrichRoutes(It.IsAny<List<GoogleRoute>>(), It.IsAny<List<BusLocation>>()))
           .Returns([enrichedRoute]);

    bazi.Setup(b => b.HasApiKeyAsync()).ReturnsAsync(true);
    bazi.Setup(b => b.GetDateFortuneAsync(It.IsAny<string>()))
        .ReturnsAsync(new FortuneData { LuckyHours = [MalaysiaTime.Now.Hour] });

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, [], cache: cache);
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
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
      new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 10 });

    var provider = new TransportProvider("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30.0, "Bus");
    var vehicle = new BusLocation { VehicleId = "BUS-NEAR", RouteId = "U60" };
    gtfs.Setup(g => g.GetBusPositionsAsync(provider.Endpoint, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GtfsFetchResult(GtfsFetchStatus.Updated, [vehicle]));

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, [provider], cache: cache);

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
    Assert.False(cache.TryGetValue("transit_cached_vehicles", out _));
  }

  [Fact]
  public async Task Reset_WithBoundedMemoryCache_ClearsBothVehiclesAndFortuneCache()
  {
    var gtfs = new Mock<IGtfsService>();
    var routing = new Mock<ILiveRoutingService>();
    var bazi = new Mock<IBaziFlowService>();

    using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
      new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 10 });

    var provider = new TransportProvider("Rapid KL", "https://feed.test/kl", 3.139, 101.6869, 30.0, "Bus");
    gtfs.Setup(g => g.GetBusPositionsAsync(provider.Endpoint, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new GtfsFetchResult(GtfsFetchStatus.Updated, [new BusLocation { VehicleId = "BUS-RESET" }]));

    bazi.Setup(b => b.HasApiKeyAsync()).ReturnsAsync(true);
    bazi.Setup(b => b.GetDateFortuneAsync(It.IsAny<string>()))
        .ReturnsAsync(new FortuneData { LuckyHours = [MalaysiaTime.Now.Hour] });

    routing.Setup(r => r.EnrichRoutes(It.IsAny<List<GoogleRoute>>(), It.IsAny<List<BusLocation>>()))
        .Returns([new EnrichedRoute { OriginalRoute = new GoogleRoute(), LiveTotalDurationSeconds = 300 }]);

    var service = new TransitTrackingService(gtfs.Object, routing.Object, bazi.Object, [provider], cache: cache);
    await service.RefreshVehiclesAsync(3.140, 101.6870, force: true);
    await service.EnrichRoutesAsync([new GoogleRoute()]);

    Assert.Single(service.CachedVehicles);
    var todayKey = MalaysiaTime.Now.ToString("yyyy-MM-dd");
    Assert.True(cache.TryGetValue("transit_cached_vehicles", out _));
    Assert.True(cache.TryGetValue($"transit_fortune_{todayKey}", out _));

    service.Reset();

    Assert.Empty(service.CachedVehicles);
    Assert.False(cache.TryGetValue("transit_cached_vehicles", out _));
    Assert.False(cache.TryGetValue($"transit_fortune_{todayKey}", out _));
  }
}
