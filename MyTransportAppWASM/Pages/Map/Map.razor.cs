
namespace MyTransportAppWASM.Pages.Map;

public partial class Map : IAsyncDisposable
{
  [Inject] private IJSRuntime JS { get; set; } = default!;
  [Inject] private ITransitTrackingService TransitTracking { get; set; } = default!;
  [Inject] private ITransportStaticDataService StaticDataService { get; set; } = default!;
  [Inject] private IConfiguration Config { get; set; } = default!;
  [CascadingParameter] private ThemeService Theme { get; set; } = default!;
  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private IPlaceResolutionService PlaceResolution { get; set; } = default!;

  private double Latitude;
  private double Longitude;

  private IJSObjectReference? mapModule;
  private DotNetObjectReference<Map>? objRef;
  private CancellationTokenSource _cts = new();
  private string origin = "";
  private string destination = "";
  private string mode = "TRANSIT";
  private bool _isDisposed = false;
  private string locationStatus = "Initializing...";
  private bool isCollapsed = false;
  private IReadOnlyList<TransportProvider> _transportProviders = Array.Empty<TransportProvider>();
  private int _selectedStaticProviderIndex = -1;
  private string _selectedStaticRouteId = "";
  private List<StaticRoute> _staticRoutes = [];
  private List<StaticDeparture> _staticDepartures = [];
  private string _selectedStaticStopId = "";
  private string _staticStopName = "";
  private string _staticStatus = "";
  private TimeSpan _pollingInterval = TimeSpan.FromSeconds(15);
  private readonly SemaphoreSlim _refreshGate = new(1, 1);
  private List<MyTransportAppWASM.Models.EnrichedRoute> _routeOptions = new();
  private int _activeRouteId = -1;
  private Task _autocompleteTask = Task.CompletedTask;
  private Task _pollingTask = Task.CompletedTask;

  private string MapContainerClass => Theme?.IsDarkMode == true ? "theme-dark" : "theme-light";
  private string MapOverlayClass => $"map-overlay {(isCollapsed ? "collapsed" : "")}".Trim();

  private void ToggleCollapse() => isCollapsed = !isCollapsed;

  private TransportStaticData? SelectedStaticData =>
    _selectedStaticProviderIndex >= 0 && _selectedStaticProviderIndex < _transportProviders.Count
      ? _transportProviders[_selectedStaticProviderIndex].StaticData : null;

  private async Task SelectStaticProvider(ChangeEventArgs args)
  {
    _selectedStaticProviderIndex = int.TryParse(args.Value?.ToString(), out var index) ? index : -1;
    _selectedStaticRouteId = "";
    _staticRoutes = [];
    _staticDepartures = [];
    _selectedStaticStopId = "";
    _staticStopName = "";
    if (mapModule != null) await mapModule.InvokeVoidAsync("clearStaticRoute");
    var source = SelectedStaticData;
    if (source == null) { _staticStatus = ""; return; }

    _staticStatus = "Loading routes...";
    try
    {
      var routes = await StaticDataService.GetRoutesAsync(source, _cts.Token);
      if (_isDisposed || SelectedStaticData != source) return;
      _staticRoutes = routes;
      _staticStatus = routes.Count == 0 ? "No routes available." : "Choose a route.";
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      if (_isDisposed || SelectedStaticData != source) return;
      _staticStatus = "Static routes unavailable.";
      Console.Error.WriteLine($"Static route list failed: {ex.Message}");
    }
  }

  private async Task SelectStaticRoute(ChangeEventArgs args)
  {
    _selectedStaticRouteId = args.Value?.ToString() ?? "";
    _staticDepartures = [];
    _selectedStaticStopId = "";
    _staticStopName = "";
    if (mapModule == null) return;
    await mapModule.InvokeVoidAsync("clearStaticRoute");
    var source = SelectedStaticData;
    var routeId = _selectedStaticRouteId;
    if (source == null || string.IsNullOrEmpty(routeId)) { _staticStatus = "Choose a route."; return; }

    _staticStatus = "Loading route map...";
    try
    {
      var geoJson = await StaticDataService.GetMapAsync(source, routeId, _cts.Token);
      if (_isDisposed || SelectedStaticData != source || _selectedStaticRouteId != routeId) return;
      await mapModule.InvokeVoidAsync("showStaticRoute", geoJson, objRef);
      _staticStatus = "Select a stop on the map for departures.";
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      if (_isDisposed || SelectedStaticData != source || _selectedStaticRouteId != routeId) return;
      _staticStatus = "Static route map unavailable.";
      Console.Error.WriteLine($"Static route map failed: {ex.Message}");
    }
  }

  [JSInvokable]
  public async Task OnStaticStopSelected(string stopId, string stopName)
  {
    var source = SelectedStaticData;
    var routeId = _selectedStaticRouteId;
    if (_isDisposed || source == null || string.IsNullOrEmpty(routeId)) return;
    _selectedStaticStopId = stopId;
    _staticStopName = string.IsNullOrWhiteSpace(stopName) ? stopId : stopName;
    _staticDepartures = [];
    _staticStatus = "Loading estimated departures...";
    StateHasChanged();
    try
    {
      var departures = await StaticDataService.GetDeparturesAsync(source, routeId, stopId, _cts.Token);
      if (_isDisposed || SelectedStaticData != source || _selectedStaticRouteId != routeId || _selectedStaticStopId != stopId) return;
      _staticDepartures = departures;
      _staticStatus = departures.Count == 0 ? "No upcoming departures for this route." : "Scheduled estimates, not live arrivals.";
      StateHasChanged();
    }
    catch (OperationCanceledException) { }
    catch (ObjectDisposedException) { }
    catch (Exception ex)
    {
      if (_isDisposed || SelectedStaticData != source || _selectedStaticRouteId != routeId || _selectedStaticStopId != stopId) return;
      _staticStatus = "Departures unavailable.";
      Console.Error.WriteLine($"Static departures failed: {ex.Message}");
      StateHasChanged();
    }
  }

  protected override Task OnInitializedAsync()
  {
    Latitude = Config.GetValue<double?>("DefaultLocation:Latitude")
        ?? throw new InvalidOperationException("Missing configuration: DefaultLocation:Latitude");
    Longitude = Config.GetValue<double?>("DefaultLocation:Longitude")
        ?? throw new InvalidOperationException("Missing configuration: DefaultLocation:Longitude");
    _transportProviders = Config.GetSection("TransportProviders").Get<List<TransportProvider>>() ?? [];

    var pollingSeconds = Math.Max(5, Config.GetValue("TransitPolling:IntervalSeconds", 15));
    _pollingInterval = TimeSpan.FromSeconds(pollingSeconds);

    if (Theme != null)
    {
      Theme.OnThemeChanged += OnThemeChanged;
    }

    // Fortune data is optional route decoration and must not delay the map's first render.
    return Task.CompletedTask;
  }

  private void OnThemeChanged()
  {
    if (!_isDisposed)
    {
      _ = InvokeAsync(UpdateMapThemeAsync);
    }
  }

  private async Task UpdateMapThemeAsync()
  {
    if (mapModule != null && !_isDisposed)
    {
      try
      {
        await mapModule.InvokeVoidAsync("setMapTheme", Theme.IsDarkMode ? "DARK" : "LIGHT");
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
      mapModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/googleMap.js");

      if (_isDisposed) return;

      // Initialize autocomplete concurrently so it's not blocked by GPS loading
      _autocompleteTask = InitializeAutocompleteAsync();
      await InitializeMapOptimisticallyAsync();

      if (_isDisposed) return;

      // Start periodic bus refresh using PeriodicTimer (WASM-friendly, no SynchronizationContext issues)
      _pollingTask = RunBusPollingLoopAsync(_cts.Token);
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
    using var timer = new PeriodicTimer(_pollingInterval);

    try
    {
      while (await timer.WaitForNextTickAsync(cancellationToken))
      {
        await InvokeAsync(() => RefreshBusPosition(force: false));
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

      await mapModule.InvokeVoidAsync("initGoogleMaps", "map", initialLat, initialLng, 14, apiKey, objRef,
        Theme.IsDarkMode ? "DARK" : "LIGHT", MalaysiaTime.IanaTimeZone);

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
        LocationService.UpdateLocation(initialLat, initialLng);
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

  private async Task<string?> ResolveAddressAsync(double lat, double lng)
  {
    string? resolved = null;
    if (mapModule != null)
    {
      try
      {
        resolved = await mapModule.InvokeAsync<string>("reverseGeocode", lat, lng);
      }
      catch (JSException)
      {
      }
    }
    if (string.IsNullOrEmpty(resolved))
    {
      resolved = await PlaceResolution.ReverseGeocodeAsync(lat, lng);
    }
    return resolved;
  }

  private async Task UpdateOriginFromLocation(double lat, double lng)
  {
    if (_isDisposed || !string.IsNullOrEmpty(origin)) return;
    try
    {
      string? resolved = await ResolveAddressAsync(lat, lng);
      if (_isDisposed || string.IsNullOrEmpty(resolved)) return;
      origin = resolved;
      if (mapModule != null)
      {
        await mapModule.InvokeVoidAsync("setAutocompleteValue", "origin-input", origin);
      }
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
    if (_isDisposed) return;
    origin = value;
    StateHasChanged();
  }

  [JSInvokable]
  public async Task UpdateUserPosition(double lat, double lng)
  {
    if (_isDisposed) return;
    LocationService.UpdateLocation(lat, lng);
    await RefreshBusPosition();
    if (_isDisposed) return;
    StateHasChanged();
  }

  [JSInvokable]
  public void UpdateDestination(string value)
  {
    if (_isDisposed) return;
    destination = value;
    StateHasChanged();
  }

  [JSInvokable]
  public void OnRouteSelected(int routeId)
  {
    if (_isDisposed || _activeRouteId == routeId) return;
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
      var resolved = await ResolveAddressAsync(position.Latitude, position.Longitude);
      if (!string.IsNullOrEmpty(resolved)) origin = resolved;

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

  private Task RefreshBusPosition() => RefreshBusPosition(force: true);

  private async Task RefreshBusPosition(bool force)
  {
    var pos = LocationService.LastKnownLocation;
    if (mapModule == null || _isDisposed || pos == null) return;
    try
    {
      if (!await _refreshGate.WaitAsync(0)) return;
    }
    catch (ObjectDisposedException)
    {
      return;
    }

    try
    {
      var result = await TransitTracking.RefreshVehiclesAsync(pos.Latitude, pos.Longitude, force, _cts.Token);
      if (_isDisposed || mapModule == null) return;

      await mapModule.InvokeVoidAsync("syncMarkers", result.Vehicles);

      locationStatus = result.StatusMessage;
      StateHasChanged();
    }
    catch (OperationCanceledException)
    {
      // Expected during disposal — suppress
    }
    catch (ObjectDisposedException)
    {
      // Expected during disposal — suppress
    }
    catch (Exception ex)
    {
      if (_isDisposed) return;
      Console.Error.WriteLine($"RefreshBusPosition failed: {ex.Message}");
    }
    finally
    {
      try
      {
        _refreshGate.Release();
      }
      catch (ObjectDisposedException) { }
    }
  }

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

      if (!string.Equals(mode, "TRANSIT", StringComparison.Ordinal))
      {
        await mapModule.InvokeVoidAsync("showRouteByName", origin, destination, mode);
        _activeRouteId = -1;
        locationStatus = $"{char.ToUpperInvariant(mode[0])}{mode[1..].ToLowerInvariant()} route ready.";
        StateHasChanged();
        return;
      }

      var googleRoutes = await mapModule.InvokeAsync<List<MyTransportAppWASM.Models.GoogleRoute>>("getTransitRoutes", origin, destination);
      
      if (googleRoutes == null || googleRoutes.Count == 0)
      {
        locationStatus = "No transit routes found.";
        StateHasChanged();
        return;
      }

      // For demonstration purposes, mark as auspicious if it's a lucky hour,
      // or alternate to show the badge regardless.
      _routeOptions = await TransitTracking.EnrichRoutesAsync(googleRoutes, _cts.Token);

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
      _routeOptions.Clear();
      _activeRouteId = -1;
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

    objRef?.Dispose();
    if (Theme != null)
    {
      Theme.OnThemeChanged -= OnThemeChanged;
    }

    // Cancel the polling loop and any in-flight HTTP calls
    await _cts.CancelAsync();
    await Task.WhenAll(_autocompleteTask, _pollingTask);
    _cts.Dispose();
    _refreshGate.Dispose();

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
