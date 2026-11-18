using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;

namespace MyTransportAppWASM.Tests;

public class LocationServiceTests
{
  private sealed class OffsetTimeProvider : TimeProvider
  {
    private long _timestamp;
    public OffsetTimeProvider(long initialTimestamp = 1_000_000) => _timestamp = initialTimestamp;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan duration) => _timestamp += (long)(duration.TotalSeconds * TimestampFrequency);
  }

  [Fact]
  public async Task GetCurrentLocationAsync_ReturnsCachedLocation_WhenWithinMaxAge()
  {
    var (service, js, _) = CreateService();
    service.UpdateLocation(3.139, 101.6869, 10.0);

    var loc = await service.GetCurrentLocationAsync(forceRefresh: false);

    Assert.NotNull(loc);
    Assert.Equal(3.139, loc.Latitude);
    Assert.Equal(101.6869, loc.Longitude);
    js.Verify(j => j.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
  }

  [Fact]
  public async Task GetCurrentLocationAsync_ReFetches_WhenCacheExpired()
  {
    var timeProvider = new OffsetTimeProvider();
    var (service, _, geoModule) = CreateService(timeProvider: timeProvider, fetchedLocation: new Location(3.150, 101.700, 5.0));
    service.UpdateLocation(3.139, 101.6869, 10.0);

    // Advance 61 seconds (exceeding 60-second cache max age)
    timeProvider.Advance(TimeSpan.FromSeconds(61));

    var loc = await service.GetCurrentLocationAsync(forceRefresh: false);

    Assert.NotNull(loc);
    Assert.Equal(3.150, loc.Latitude);
    Assert.Equal(101.700, loc.Longitude);
    geoModule.Verify(m => m.InvokeAsync<Location?>("getUserLocation", It.IsAny<object[]>()), Times.Once);
  }

  [Fact]
  public async Task GetCurrentLocationAsync_WithForceRefresh_BypassesCacheEvenWhenFresh()
  {
    var (service, _, geoModule) = CreateService(fetchedLocation: new Location(3.200, 101.750, 8.0));
    service.UpdateLocation(3.139, 101.6869, 10.0);

    var loc = await service.GetCurrentLocationAsync(forceRefresh: true);

    Assert.NotNull(loc);
    Assert.Equal(3.200, loc.Latitude);
    Assert.Equal(101.750, loc.Longitude);
    geoModule.Verify(m => m.InvokeAsync<Location?>("getUserLocation", It.IsAny<object[]>()), Times.Once);
  }

  [Fact]
  public async Task GetCurrentLocationAsync_HydratesLastKnownLocationFromCachedStorage_BeforeLiveLookupCompletes()
  {
    var liveTcs = new TaskCompletionSource<Location?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var (service, _, geoModule) = CreateService(importModule: true);
    geoModule.Setup(m => m.InvokeAsync<Location?>("getCachedLocation", It.IsAny<object[]>()))
        .ReturnsAsync(new Location(5.4141, 100.3288, 25.0));
    geoModule.Setup(m => m.InvokeAsync<Location?>("getUserLocation", It.IsAny<object[]>()))
        .Returns(() => new ValueTask<Location?>(liveTcs.Task));

    var pendingTask = service.GetCurrentLocationAsync(forceRefresh: false);

    // Even while live GPS is still pending, LastKnownLocation is already hydrated from localStorage cache
    Assert.NotNull(service.LastKnownLocation);
    Assert.Equal(5.4141, service.LastKnownLocation.Latitude);
    Assert.Equal(100.3288, service.LastKnownLocation.Longitude);

    liveTcs.SetResult(null);
    Assert.Null(await pendingTask);
    Assert.Equal(5.4141, service.LastKnownLocation.Latitude);
  }

  [Fact]
  public void LocationService_Throws_WhenGeolocationSecondsIsZeroOrNegative() =>
      Assert.Throws<InvalidOperationException>(() => CreateService(geolocationSeconds: 0));

  private static (LocationService Service, Mock<IJSRuntime> Js, Mock<IJSObjectReference> Module) CreateService(
      int geolocationSeconds = 60,
      TimeProvider? timeProvider = null,
      bool importModule = false,
      Location? fetchedLocation = null)
  {
    var js = new Mock<IJSRuntime>();
    var module = new Mock<IJSObjectReference>();
    if (importModule || fetchedLocation is not null)
    {
      js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
          .ReturnsAsync(module.Object);
    }
    if (fetchedLocation is not null)
    {
      module.Setup(m => m.InvokeAsync<Location?>("getUserLocation", It.IsAny<object[]>()))
          .ReturnsAsync(fetchedLocation);
    }

    var options = Options.Create(new CacheExpirationOptions
    {
      Map = new MapCacheExpirationOptions { GeolocationSeconds = geolocationSeconds }
    });
    return (new LocationService(js.Object, options, timeProvider), js, module);
  }
}
