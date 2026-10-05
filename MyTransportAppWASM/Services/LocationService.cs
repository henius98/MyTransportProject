
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Services
{
  public class LocationService : ILocationService, IAsyncDisposable
  {
    private readonly IJSRuntime _js;
    private readonly long _cacheMaxAgeMilliseconds;
    private IJSObjectReference? _geoModule;
    public Location? LastKnownLocation { get; private set; }
    public event Action<Location>? OnLocationChanged;

    public LocationService(IJSRuntime js, IOptions<CacheExpirationOptions> cacheExpiration)
    {
      _js = js;
      var cacheSeconds = cacheExpiration.Value.Map.GeolocationSeconds;
      _cacheMaxAgeMilliseconds = cacheSeconds > 0
        ? checked((long)cacheSeconds * 1000)
        : throw new InvalidOperationException(
          $"Cache expiration setting '{nameof(CacheExpirationOptions.Map)}:{nameof(MapCacheExpirationOptions.GeolocationSeconds)}' must be greater than zero.");
    }

    public async Task<Location?> GetCurrentLocationAsync(bool forceRefresh = false)
    {
      if (!forceRefresh && LastKnownLocation != null)
      {
        return LastKnownLocation;
      }

      try
      {
        if (_geoModule == null)
        {
          _geoModule = await _js.InvokeAsync<IJSObjectReference>("import", "./js/geolocation.js");
        }

        var location = await _geoModule.InvokeAsync<Location?>("getUserLocation", new { cacheMaxAge = _cacheMaxAgeMilliseconds });
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
      OnLocationChanged?.Invoke(LastKnownLocation);
    }

    public async ValueTask DisposeAsync()
    {
      if (_geoModule != null)
      {
        await _geoModule.DisposeAsync();
      }
    }
  }
}
