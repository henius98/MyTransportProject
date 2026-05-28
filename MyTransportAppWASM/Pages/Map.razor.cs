using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MyTransportAppWASM.Models.Transport;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Pages;

public partial class Map : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IGtfsService GtfsService { get; set; } = default!;
    [Inject] private IConfiguration Config { get; set; } = default!;
    [Inject] private ThemeService Theme { get; set; } = default!;
    [Inject] private ILocationService LocationService { get; set; } = default!;

    private double FallbackLatitude;
    private double FallbackLongitude;

    private IJSObjectReference? mapModule;
    private DotNetObjectReference<Map>? objRef;
    private DateTime lastUpdated;
    private CancellationTokenSource _cts = new();
    private string origin = "";
    private string destination = "";
    private string mode = "TRANSIT";
    private bool _isDisposed = false;
    private string locationStatus = "Initializing...";
    private bool isCollapsed = false;
    private Dictionary<string, List<object>> _providerVehicleCache = new();

    private void ToggleCollapse() => isCollapsed = !isCollapsed;

    protected override void OnInitialized()
    {
        FallbackLatitude = Config.GetValue<double>("MapSettings:FallbackLatitude", 1.4632);
        FallbackLongitude = Config.GetValue<double>("MapSettings:FallbackLongitude", 103.7644);
        Theme.OnThemeChanged += OnThemeChanged;
    }

    private async void OnThemeChanged()
    {
        if (mapModule != null && !_isDisposed)
        {
            try
            {
                await mapModule.InvokeVoidAsync("setMapTheme", Theme.IsDarkMode ? "DARK" : "LIGHT");
                if (!_isDisposed) await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                if (!_isDisposed) Console.Error.WriteLine($"Theme update failed: {ex.Message}");
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        try
        {
            objRef = DotNetObjectReference.Create(this);
            mapModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/googleMap.js?v=" + DateTime.Now.Ticks);

            if (_isDisposed) return;

            // Initialize autocomplete concurrently so it's not blocked by GPS loading
            _ = InitializeAutocompleteAsync();
            await InitializeMapOptimisticallyAsync();

            // Start periodic bus refresh using PeriodicTimer (WASM-friendly, no SynchronizationContext issues)
            _ = RunBusPollingLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Init error: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces System.Timers.Timer with PeriodicTimer for cleaner cancellation
    /// and no cross-thread marshalling issues in Blazor WASM.
    /// </summary>
    private async Task RunBusPollingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await InvokeAsync(RefreshBusPosition);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — timer was cancelled via CancellationTokenSource
        }
    }

    private async Task InitializeMapOptimisticallyAsync()
    {
        if (mapModule is null || _isDisposed) return;

        try
        {
            var apiKey = Config["GoogleMaps:ApiKey"];
            var initialLat = LocationService.LastKnownLocation?.Latitude ?? FallbackLatitude;
            var initialLng = LocationService.LastKnownLocation?.Longitude ?? FallbackLongitude;

            await mapModule.InvokeVoidAsync("initGoogleMaps", "map", initialLat, initialLng, 14, apiKey, objRef, Theme.IsDarkMode ? "DARK" : "LIGHT");

            locationStatus = "Loading GPS...";
            StateHasChanged();

            var position = await LocationService.GetCurrentLocationAsync();
            if (_isDisposed) return;

            if (position is not null)
            {
                await mapModule.InvokeVoidAsync("updateUserMarker", position.Latitude, position.Longitude);
                locationStatus = "Live";
                await UpdateOriginFromLocation(position.Latitude, position.Longitude);
            }
            else
            {
                locationStatus = "Using default location";
            }

            await RefreshBusPosition();
            StateHasChanged();
        }
        catch (JSException ex)
        {
            locationStatus = "Map error";
            Console.Error.WriteLine($"initGoogleMaps failed: {ex.Message}");
        }
    }

    private async Task UpdateOriginFromLocation(double lat, double lng)
    {
        if (mapModule == null || _isDisposed || !string.IsNullOrEmpty(origin)) return;
        try
        {
            origin = await mapModule.InvokeAsync<string>("reverseGeocode", lat, lng);
            if (_isDisposed || mapModule == null) return;
            await mapModule.InvokeVoidAsync("setAutocompleteValue", "origin-input", origin);
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            Console.Error.WriteLine($"Reverse geocode failed: {ex.Message}");
        }
    }

    [JSInvokable]
    public void UpdateOrigin(string value)
    {
        origin = value;
        StateHasChanged();
    }

    [JSInvokable]
    public async Task UpdateUserPosition(double lat, double lng)
    {
        // This could be used to sync back to LocationService if needed
        await RefreshBusPosition();
        StateHasChanged();
    }

    [JSInvokable]
    public void UpdateDestination(string value)
    {
        destination = value;
        StateHasChanged();
    }

    private async Task InitializeAutocompleteAsync()
    {
        if (mapModule is null || _isDisposed || objRef is null) return;
        var apiKey = Config["GoogleMaps:ApiKey"];

        try
        {
            await Task.WhenAll(
                mapModule.InvokeVoidAsync("initAutocomplete", "origin-input", objRef, "UpdateOrigin", apiKey).AsTask(),
                mapModule.InvokeVoidAsync("initAutocomplete", "destination-input", objRef, "UpdateDestination", apiKey).AsTask()
            );
        }
        catch (Exception ex)
        {
            if (!_isDisposed) Console.Error.WriteLine($"Autocomplete init failed: {ex.Message}");
        }
    }

    private async Task RequestManualLocationUpdate()
    {
        var position = await LocationService.GetCurrentLocationAsync();
        if (_isDisposed || position == null || mapModule == null) return;

        try
        {
            await mapModule.InvokeVoidAsync("updateUserMarker", position.Latitude, position.Longitude);
            origin = await mapModule.InvokeAsync<string>("reverseGeocode", position.Latitude, position.Longitude);
            
            if (_isDisposed || mapModule == null) return;
            await mapModule.InvokeVoidAsync("setAutocompleteValue", "origin-input", origin);
            await RefreshBusPosition();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            Console.Error.WriteLine($"Manual location update failed: {ex.Message}");
        }
    }

    private async Task RefreshBusPosition()
    {
        var pos = LocationService.LastKnownLocation;
        if (mapModule == null || _isDisposed || pos == null) return;

        try
        {
            var providers = Config.GetSection("TransportProviders").Get<List<TransportProvider>>() ?? new();
            var nearbyProviders = providers.Where(p =>
                CalculateDistance(pos.Latitude, pos.Longitude, p.CenterLat, p.CenterLng) <= p.RadiusKm
            ).ToList();

            if (!nearbyProviders.Any())
            {
                locationStatus = "No bus providers found nearby.";
                StateHasChanged();
                return;
            }

            var cancellationToken = _cts.Token;

            var tasks = nearbyProviders.Select(async provider =>
            {
                var feed = await GtfsService.GetBusPositionsAsync(provider.Endpoint, cancellationToken);
                if (_isDisposed || feed?.Entity == null) return;

                _providerVehicleCache[provider.Endpoint] = feed.Entity
                    .Where(e => e.Vehicle?.Position != null)
                    .Select(e => (object)new
                    {
                        tripId = e.Vehicle.Trip?.TripId,
                        routeId = e.Vehicle.Trip?.RouteId,
                        vehicleId = e.Vehicle.Vehicle.Id,
                        lat = e.Vehicle.Position.Latitude,
                        lng = e.Vehicle.Position.Longitude,
                        bearing = e.Vehicle.Position.Bearing,
                        speed = e.Vehicle.Position.Speed,
                        timestamp = e.Vehicle.Timestamp
                    }).ToList();
            });

            await Task.WhenAll(tasks);

            if (_isDisposed || mapModule == null) return;

            var allVehicles = nearbyProviders
                .Where(p => _providerVehicleCache.ContainsKey(p.Endpoint))
                .SelectMany(p => _providerVehicleCache[p.Endpoint])
                .ToList();

            await mapModule.InvokeVoidAsync("syncMarkers", allVehicles);
            lastUpdated = DateTime.Now;
            locationStatus = $"Buses updated: {lastUpdated:HH:mm:ss} ({nearbyProviders.Count} source(s))";
            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal — suppress
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            Console.Error.WriteLine($"RefreshBusPosition failed: {ex.Message}");
        }
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var R = 6371;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private double ToRadians(double deg) => deg * (Math.PI / 180);

    private async Task ShowRoute()
    {
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(destination))
        {
            locationStatus = "Please enter origin and destination.";
            StateHasChanged();
            return;
        }

        if (mapModule == null || _isDisposed) return;
        
        try
        {
            locationStatus = "Calculating route...";
            StateHasChanged();
            await mapModule.InvokeVoidAsync("showRouteByName", origin, destination, mode);
            locationStatus = "Route displayed.";
            StateHasChanged();
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            locationStatus = "Route failed.";
            Console.Error.WriteLine($"Route display failed: {ex.Message}");
            StateHasChanged();
        }
    }

    private async Task ClearRoute()
    {
        if (mapModule == null || _isDisposed) return;
        try
        {
            await mapModule.InvokeVoidAsync("clearRoute");
            locationStatus = "Route cleared.";
            StateHasChanged();
        }
        catch (Exception ex)
        {
            if (_isDisposed) return;
            Console.Error.WriteLine($"Clear route failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Theme.OnThemeChanged -= OnThemeChanged;
        
        // Cancel the polling loop and any in-flight HTTP calls
        await _cts.CancelAsync();
        _cts.Dispose();

        objRef?.Dispose();
        
        if (mapModule is not null)
        {
            try
            {
                await mapModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { /* Ignore during disposal */ }
        }
    }
}
