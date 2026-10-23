
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Services
{
  public sealed class LocationService : ILocationService, IAsyncDisposable
  {
    private readonly IJSRuntime _js;
    private readonly long _cacheMaxAgeMilliseconds;
    private readonly SemaphoreSlim _moduleLock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private IJSObjectReference? _geoModule;
    private long _lastLocationTimestamp;
    public Location? LastKnownLocation { get; private set; }

    public LocationService(IJSRuntime js, IOptions<CacheExpirationOptions> cacheExpiration, TimeProvider? timeProvider = null)
    {
      _js = js;
      _timeProvider = timeProvider ?? TimeProvider.System;
      var cacheSeconds = cacheExpiration.Value.Map.GeolocationSeconds;
      _cacheMaxAgeMilliseconds = cacheSeconds > 0
        ? checked((long)cacheSeconds * 1000)
        : throw new InvalidOperationException(
          $"Cache expiration setting '{nameof(CacheExpirationOptions.Map)}:{nameof(MapCacheExpirationOptions.GeolocationSeconds)}' must be greater than zero.");
    }

    private async ValueTask<IJSObjectReference?> GetGeoModuleAsync()
    {
      if (_isDisposed) return null;
      if (_geoModule != null) return _geoModule;
      await _moduleLock.WaitAsync();
      try
      {
        if (_isDisposed) return null;
        return _geoModule ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/geolocation.js");
      }
      finally
      {
        _moduleLock.Release();
      }
    }

    public async Task<Location?> GetCurrentLocationAsync(bool forceRefresh = false)
    {
      if (_isDisposed) return null;
      if (!forceRefresh && LastKnownLocation != null && _lastLocationTimestamp != 0
          && _timeProvider.GetElapsedTime(_lastLocationTimestamp).TotalMilliseconds < _cacheMaxAgeMilliseconds)
      {
        return LastKnownLocation;
      }

      try
      {
        var geoModule = await GetGeoModuleAsync();
        if (geoModule == null || _isDisposed) return null;
        var location = await geoModule.InvokeAsync<Location?>("getUserLocation", new
        {
          cacheMaxAge = forceRefresh ? 0 : _cacheMaxAgeMilliseconds,
          useCache = !forceRefresh
        });
        if (_isDisposed) return null;
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

    private bool _isDisposed;

    public async ValueTask DisposeAsync()
    {
      if (_isDisposed) return;
      _isDisposed = true;

      if (_geoModule != null)
      {
        await _geoModule.DisposeAsync();
      }
      _moduleLock.Dispose();
    }
  }
}
