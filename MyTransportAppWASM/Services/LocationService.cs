
namespace MyTransportAppWASM.Services
{
  public class LocationService : ILocationService, IAsyncDisposable
  {
    private readonly IJSRuntime _js;
    private IJSObjectReference? _geoModule;
    public Location? LastKnownLocation { get; private set; }
    public event Action<Location>? OnLocationChanged;

    public LocationService(IJSRuntime js)
    {
      _js = js;
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

        var location = await _geoModule.InvokeAsync<Location?>("getUserLocation");
        if (location != null)
        {
          LastKnownLocation = location;
          OnLocationChanged?.Invoke(location);
        }
        return location;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"LocationService error: {ex.Message}");
        return null;
      }
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
