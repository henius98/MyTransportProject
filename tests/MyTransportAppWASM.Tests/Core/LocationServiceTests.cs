using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;

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
        var js = new Mock<IJSRuntime>();
        var options = Options.Create(new CacheExpirationOptions
        {
            Map = new MapCacheExpirationOptions { GeolocationSeconds = 60 }
        });

        var service = new LocationService(js.Object, options);
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
        var js = new Mock<IJSRuntime>();
        var geoModule = new Mock<IJSObjectReference>();
        var freshLocation = new Location(3.150, 101.700, 5.0);

        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .ReturnsAsync(geoModule.Object);
        geoModule.Setup(m => m.InvokeAsync<Location?>("getUserLocation", It.IsAny<object[]>()))
            .ReturnsAsync(freshLocation);

        var timeProvider = new OffsetTimeProvider();
        var options = Options.Create(new CacheExpirationOptions
        {
            Map = new MapCacheExpirationOptions { GeolocationSeconds = 60 }
        });

        var service = new LocationService(js.Object, options, timeProvider);
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
        var js = new Mock<IJSRuntime>();
        var geoModule = new Mock<IJSObjectReference>();
        var refreshedLocation = new Location(3.200, 101.750, 8.0);

        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .ReturnsAsync(geoModule.Object);
        geoModule.Setup(m => m.InvokeAsync<Location?>("getUserLocation", It.IsAny<object[]>()))
            .ReturnsAsync(refreshedLocation);

        var options = Options.Create(new CacheExpirationOptions
        {
            Map = new MapCacheExpirationOptions { GeolocationSeconds = 60 }
        });

        var service = new LocationService(js.Object, options);
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
        var js = new Mock<IJSRuntime>();
        var geoModule = new Mock<IJSObjectReference>();
        var cachedLocation = new Location(5.4141, 100.3288, 25.0);
        var liveTcs = new TaskCompletionSource<Location?>(TaskCreationOptions.RunContinuationsAsynchronously);

        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .ReturnsAsync(geoModule.Object);
        geoModule.Setup(m => m.InvokeAsync<Location?>("getCachedLocation", It.IsAny<object[]>()))
            .ReturnsAsync(cachedLocation);
        geoModule.Setup(m => m.InvokeAsync<Location?>("getUserLocation", It.IsAny<object[]>()))
            .Returns(() => new ValueTask<Location?>(liveTcs.Task));

        var options = Options.Create(new CacheExpirationOptions
        {
            Map = new MapCacheExpirationOptions { GeolocationSeconds = 60 }
        });

        var service = new LocationService(js.Object, options);
        var pendingTask = service.GetCurrentLocationAsync(forceRefresh: false);

        // Even while live GPS is still pending, LastKnownLocation is already hydrated from localStorage cache
        Assert.NotNull(service.LastKnownLocation);
        Assert.Equal(5.4141, service.LastKnownLocation.Latitude);
        Assert.Equal(100.3288, service.LastKnownLocation.Longitude);

        liveTcs.SetResult(null);
        var result = await pendingTask;

        Assert.Null(result);
        Assert.Equal(5.4141, service.LastKnownLocation.Latitude);
    }

    [Fact]
    public void LocationService_Throws_WhenGeolocationSecondsIsZeroOrNegative()
    {
        var js = new Mock<IJSRuntime>();
        var options = Options.Create(new CacheExpirationOptions
        {
            Map = new MapCacheExpirationOptions { GeolocationSeconds = 0 }
        });

        Assert.Throws<InvalidOperationException>(() => new LocationService(js.Object, options));
    }
}
