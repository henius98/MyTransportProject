
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Core.Services
{
  public sealed class LocationService : ILocationService, IAsyncDisposable
  {
    private readonly JsModuleLoader _geoModule;
    private readonly long _cacheMaxAgeMilliseconds;
    private readonly TimeProvider _timeProvider;
    private long _lastLocationTimestamp;
    public Location? LastKnownLocation { get; private set; }

    public LocationService(IJSRuntime js, IOptions<CacheExpirationOptions> cacheExpiration, TimeProvider? timeProvider = null)
    {
      _geoModule = new JsModuleLoader(js, "./js/geolocation.js");
      _timeProvider = timeProvider ?? TimeProvider.System;
      var cacheSeconds = cacheExpiration.Value.Map.GeolocationSeconds;
      _cacheMaxAgeMilliseconds = cacheSeconds > 0
        ? checked((long)cacheSeconds * 1000)
        : throw new InvalidOperationException(
          $"Cache expiration setting '{nameof(CacheExpirationOptions.Map)}:{nameof(MapCacheExpirationOptions.GeolocationSeconds)}' must be greater than zero.");
    }

    public async Task<Location?> GetCurrentLocationAsync(bool forceRefresh = false)
    {
      if (_geoModule.IsDisposed) return null;
      if (!forceRefresh && LastKnownLocation != null && _lastLocationTimestamp != 0
          && _timeProvider.GetElapsedTime(_lastLocationTimestamp).TotalMilliseconds < _cacheMaxAgeMilliseconds)
      {
        return LastKnownLocation;
      }

      try
      {
        var geoModule = await _geoModule.TryGetAsync();
        if (geoModule == null || _geoModule.IsDisposed) return null;

        if (LastKnownLocation == null)
        {
          var cached = await geoModule.InvokeAsync<Location?>("getCachedLocation");
          if (_geoModule.IsDisposed) return null;
          if (cached != null && LastKnownLocation == null)
          {
            LastKnownLocation = cached;
          }
        }

        var location = await geoModule.InvokeAsync<Location?>("getUserLocation", new GeolocationRequestOptions(
          CacheMaxAge: forceRefresh ? 0 : _cacheMaxAgeMilliseconds,
          UseCache: !forceRefresh,
          EnableHighAccuracy: forceRefresh));
        if (_geoModule.IsDisposed) return null;
        if (location != null)
        {
          UpdateLocation(location.Latitude, location.Longitude, location.Accuracy);
        }
        return location;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"LocationService error: {ex.Message}");
        return null;
      }
    }

    public void UpdateLocation(double latitude, double longitude, double? accuracy = null)
    {
      LastKnownLocation = new Location(latitude, longitude, accuracy);
      _lastLocationTimestamp = _timeProvider.GetTimestamp();
    }

    public ValueTask DisposeAsync() => _geoModule.DisposeAsync();
  }
}
