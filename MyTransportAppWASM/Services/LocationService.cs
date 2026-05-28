using Microsoft.JSInterop;
using System;
using System.Threading.Tasks;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
    public class LocationService : ILocationService, IAsyncDisposable
    {
        private readonly IJSRuntime _js;
        private IJSObjectReference? _geoModule;
        private Location? _lastKnownLocation;

        public Location? LastKnownLocation => _lastKnownLocation;
        public event Action<Location>? OnLocationChanged;

        public LocationService(IJSRuntime js)
        {
            _js = js;
        }

        public async Task<Location?> GetCurrentLocationAsync()
        {
            try
            {
                if (_geoModule == null)
                {
                    _geoModule = await _js.InvokeAsync<IJSObjectReference>("import", "./js/geolocation.js");
                }

                var location = await _geoModule.InvokeAsync<Location?>("getUserLocation");
                if (location != null)
                {
                    _lastKnownLocation = location;
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
