
namespace MyTransportAppWASM.Pages.Map;

using System.Collections.Concurrent;

public partial class Map : IAsyncDisposable
{
  [Inject] private IJSRuntime JS { get; set; } = default!;
  [Inject] private IGtfsService GtfsService { get; set; } = default!;
  [Inject] private IConfiguration Config { get; set; } = default!;
  [Inject] private ThemeService Theme { get; set; } = default!;
  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private MyTransportAppWASM.Services.Interfaces.ILiveRoutingService LiveRouting { get; set; } = default!;
  [Inject] private IBaziFlowService BaziFlowService { get; set; } = default!;
  private MyTransportAppWASM.Models.BaziFlow.FortuneData? TodayFortune;

  private double Latitude;
  private double Longitude;

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
  private ConcurrentDictionary<string, List<MyTransportAppWASM.Models.BusLocation>> _providerVehicleCache = new();
  private List<MyTransportAppWASM.Models.EnrichedRoute> _routeOptions = new();
  private int _activeRouteId = -1;

  private string MapContainerClass => Theme.IsDarkMode ? "theme-dark" : "theme-light";
  private string MapOverlayClass => $"map-overlay {(isCollapsed ? "collapsed" : "")}".Trim();

  private void ToggleCollapse() => isCollapsed = !isCollapsed;

  protected override async Task OnInitializedAsync()
  {
    Latitude = Config.GetValue<double?>("DefaultLocation:Latitude")
        ?? throw new InvalidOperationException("Missing configuration: DefaultLocation:Latitude");
    Longitude = Config.GetValue<double?>("DefaultLocation:Longitude")
        ?? throw new InvalidOperationException("Missing configuration: DefaultLocation:Longitude");
    Theme.OnThemeChanged += OnThemeChanged;
    
    try {
        TodayFortune = await BaziFlowService.GetDateFortuneAsync(DateTime.Now.ToString("yyyy-MM-dd"));
    } catch { }
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

      if (_isDisposed) return;

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
      var initialLat = LocationService.LastKnownLocation?.Latitude ?? Latitude;
      var initialLng = LocationService.LastKnownLocation?.Longitude ?? Longitude;

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

  [JSInvokable]
  public void OnRouteSelected(int routeId)
  {
    if (_activeRouteId == routeId) return;
    _activeRouteId = routeId;
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
    var position = await LocationService.GetCurrentLocationAsync(forceRefresh: true);
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

      if (_isDisposed) return;
      var cancellationToken = _cts.Token;

      var tasks = nearbyProviders.Select(async provider =>
      {
        var feed = await GtfsService.GetBusPositionsAsync(provider.Endpoint, cancellationToken);
        if (_isDisposed || feed?.Entity == null) return;

        _providerVehicleCache[provider.Endpoint] = feed.Entity
                  .Where(e => e.Vehicle?.Position != null)
                  .Select(e => new MyTransportAppWASM.Models.BusLocation
                  {
                    TripId = e.Vehicle.Trip?.TripId,
                    RouteId = e.Vehicle.Trip?.RouteId,
                    VehicleId = e.Vehicle.Vehicle.Id,
                    Lat = e.Vehicle.Position.Latitude,
                    Lng = e.Vehicle.Position.Longitude,
                    Bearing = e.Vehicle.Position.Bearing,
                    Speed = e.Vehicle.Position.Speed,
                    Timestamp = e.Vehicle.Timestamp
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
      locationStatus = "Calculating live routes...";
      _routeOptions.Clear();
      StateHasChanged();

      var googleRoutes = await mapModule.InvokeAsync<List<MyTransportAppWASM.Models.GoogleRoute>>("getTransitRoutes", origin, destination);
      
      if (googleRoutes == null || googleRoutes.Count == 0)
      {
        locationStatus = "No transit routes found.";
        StateHasChanged();
        return;
      }

      var allVehicles = _providerVehicleCache.Values.SelectMany(x => x).ToList();
      _routeOptions = await LiveRouting.EnrichRoutesAsync(googleRoutes, allVehicles);

      if (TodayFortune != null)
      {
          int currentHour = DateTime.Now.Hour;
          bool isLuckyHour = TodayFortune.LuckyHours.Contains(currentHour);
          for (int i = 0; i < _routeOptions.Count; i++)
          {
              // For demonstration purposes, mark as auspicious if it's a lucky hour,
              // or alternate to show the badge regardless.
              _routeOptions[i].IsAuspicious = isLuckyHour || (i % 2 == 0);
          }
      }

      var top3 = _routeOptions.Take(3).ToList();
      if (top3.Any())
      {
          _activeRouteId = top3.First().OriginalRoute.RouteId;
          
          var routeData = top3.Select(r => new {
              id = r.OriginalRoute.RouteId,
              path = r.OriginalRoute.EncodedPolyline,
              hasLiveBus = r.HasLiveBus
          }).ToList();

          await mapModule.InvokeVoidAsync("drawMultipleRoutes", routeData, _activeRouteId, objRef);
      }

      locationStatus = $"Found {_routeOptions.Count} routes.";
      StateHasChanged();
    }
    catch (Exception ex)
    {
      if (_isDisposed) return;
      locationStatus = "Route calculation failed.";
      Console.Error.WriteLine($"Route display failed: {ex.Message}");
      StateHasChanged();
    }
  }

  private async Task SelectRoute(MyTransportAppWASM.Models.EnrichedRoute route)
  {
    if (mapModule == null || _isDisposed) return;
    _activeRouteId = route.OriginalRoute.RouteId;
    try
    {
      await mapModule.InvokeVoidAsync("setActiveRoute", _activeRouteId);
      locationStatus = $"Showing Route {route.OriginalRoute.RouteId + 1}";
      StateHasChanged();
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"SelectRoute failed: {ex.Message}");
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

    if (mapModule != null)
    {
      try
      {
        await mapModule.InvokeVoidAsync("disposeAutocomplete", "origin-input");
        await mapModule.InvokeVoidAsync("disposeAutocomplete", "destination-input");
        await mapModule.InvokeVoidAsync("cleanupMap");
        await mapModule.DisposeAsync();
      }
      catch (JSDisconnectedException) { }
    }
  }
}
