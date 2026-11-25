using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyTransportAppWASM.Features.Transit;

public partial class Map : FeatureComponentBase, IAsyncDisposable
{
  [Inject] private IJSRuntime JS { get; set; } = default!;
  [Inject] private IConfiguration Config { get; set; } = default!;
  [CascadingParameter] private ThemeService Theme { get; set; } = default!;
  [CascadingParameter] private LanguageService Language { get; set; } = default!;
  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private ILogger<Map> Logger { get; set; } = NullLogger<Map>.Instance;

  private TransitMapSession Session => GetFeatureService<TransitMapSession>();
  private IPlaceResolutionService PlaceResolution => GetFeatureService<IPlaceResolutionService>();

  private IJSObjectReference? mapModule;
  private DotNetObjectReference<Map>? objRef;
  private readonly CancellationTokenSource _cts = new();
  private string origin = "";
  private string destination = "";
  private string mode = "TRANSIT";
  private string departureMode = "now";
  private DateTime departureDateTime = MalaysiaTime.Now.DateTime;
  private string activeTab = "tracker";
  private bool _isDisposed;
  private bool isCollapsed;
  private bool isSearchPanelCollapsed;
  private Task _autocompleteTask = Task.CompletedTask;
  private Task _pollingTask = Task.CompletedTask;

  private sealed record TravelModeOption(string Value, string Icon, string LabelKey);
  private static readonly TravelModeOption[] TravelModeOptions =
  [
    new("TRANSIT", "🚌", "ModeTransit"),
    new("DRIVING", "🚗", "ModeDriving"),
    new("WALKING", "🚶", "ModeWalking"),
    new("BICYCLING", "🚲", "ModeBicycling"),
  ];

  private string MapContainerClass => Theme?.IsDarkMode == true ? "theme-dark" : "theme-light";
  private string MapOverlayClass => $"map-overlay {(isCollapsed ? "collapsed" : "")}".Trim();

  private void ToggleCollapse() => isCollapsed = !isCollapsed;
  private void ExpandSearchPanel() => isSearchPanelCollapsed = false;
  private void SelectTab(string tab) => activeTab = tab;

  private string SearchSummaryText
  {
    get
    {
      var from = origin.Trim();
      var to = destination.Trim();
      if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
      {
        return $"{from} → {to}";
      }
      return Language["ExpandSearchPanel"];
    }
  }

  private async Task SelectTravelMode(string nextMode)
  {
    if (string.Equals(mode, nextMode, StringComparison.Ordinal)) return;
    mode = nextMode;
    if (!string.IsNullOrWhiteSpace(origin) && !string.IsNullOrWhiteSpace(destination))
    {
      await ShowRoute();
    }
  }

  private async Task SwapOriginAndDestination()
  {
    (origin, destination) = (destination, origin);
    if (mapModule != null && !_isDisposed)
    {
      await mapModule.InvokeVoidAsync("swapAutocompleteLocations", "origin-input", "destination-input");
    }
    if (!string.IsNullOrWhiteSpace(origin) && !string.IsNullOrWhiteSpace(destination) && Session.RouteOptions.Count > 0)
    {
      await ShowRoute();
    }
  }

  private Task SelectStaticProvider(ChangeEventArgs args)
  {
    var index = int.TryParse(args.Value?.ToString(), out var parsed) ? parsed : -1;
    return UpdateStaticOverlayAsync(() => Session.SelectStaticProviderAsync(index, _cts.Token));
  }

  private Task SelectStaticRoute(ChangeEventArgs args)
  {
    var routeId = args.Value?.ToString() ?? string.Empty;
    return UpdateStaticOverlayAsync(() => Session.SelectStaticRouteAsync(routeId, _cts.Token));
  }

  private int _staticOverlaySeq;

  private async Task UpdateStaticOverlayAsync(Func<Task<string?>> loadGeoJsonAsync, bool preserveSelectedVehicle = false)
  {
    var seq = ++_staticOverlaySeq;
    try
    {
      if (mapModule != null && !_isDisposed)
      {
        await (preserveSelectedVehicle
          ? mapModule.InvokeVoidAsync("clearStaticRoute", true)
          : mapModule.InvokeVoidAsync("clearStaticRoute"));
      }

      var geoJson = await loadGeoJsonAsync();
      if (!_isDisposed && mapModule != null && seq == _staticOverlaySeq)
      {
        if (geoJson != null)
        {
          await mapModule.InvokeVoidAsync("showStaticRoute", geoJson);
        }
        await SyncMarkersFromCacheAsync();
      }
    }
    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or JSException)
    {
      if (!_isDisposed && ex is JSException)
      {
        Logger.LogDebug("Static route map update failed: {Message}", ex.Message);
      }
    }
  }

  [JSInvokable]
  public async Task OnVehicleSelected(string providerName, string routeId, string routeShortName)
  {
    if (_isDisposed || !Session.HasStaticDataForProvider(providerName)) return;
    await UpdateStaticOverlayAsync(
      () => Session.SelectVehicleRouteAsync(providerName, routeId, routeShortName, _cts.Token),
      preserveSelectedVehicle: true);
    if (!_isDisposed) StateHasChanged();
  }

  [JSInvokable]
  public async Task OnVehicleDeselected(string providerName)
  {
    if (_isDisposed || !Session.HasStaticDataForProvider(providerName)) return;
    if (!Session.ClearVehicleRouteSelection(providerName)) return;
    await UpdateStaticOverlayAsync(() => Task.FromResult<string?>(null));
    if (!_isDisposed) StateHasChanged();
  }

  protected override Task OnInitializedAsync()
  {
    Session.ResetAuthFailure();

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
        if (!_isDisposed) Logger.LogDebug("Theme update failed: {Message}", ex.Message);
      }
    }
  }

  protected override async Task OnAfterRenderAsync(bool firstRender)
  {
    if (!firstRender) return;

    try
    {
      objRef = DotNetObjectReference.Create(this);
      // Start GPS/cache lookup concurrently with module loading so initial center is ready sooner
      var locationTask = LocationService.GetCurrentLocationAsync();
      mapModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/googleMap.js");

      if (_isDisposed)
      {
        try
        {
          await mapModule.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException or OperationCanceledException) { }
        mapModule = null;
        return;
      }

      // Initialize autocomplete concurrently so it's not blocked by GPS loading
      _autocompleteTask = InitializeAutocompleteAsync();
      await InitializeMapOptimisticallyAsync(locationTask);

      if (_isDisposed) return;

      // Start periodic bus refresh using PeriodicTimer (WASM-friendly, no SynchronizationContext issues)
      _pollingTask = RunBusPollingLoopAsync(_cts.Token);
    }
    catch (Exception ex)
    {
      Logger.LogError("Init error: {Message}", ex.Message);
    }
  }

  /// <summary>
  /// Replaces System.Timers.Timer with PeriodicTimer for cleaner cancellation
  /// and no cross-thread marshalling issues in Blazor WASM.
  /// </summary>
  private async Task RunBusPollingLoopAsync(CancellationToken cancellationToken)
  {
    using var timer = new PeriodicTimer(Session.PollingInterval);

    try
    {
      while (await timer.WaitForNextTickAsync(cancellationToken))
      {
        if (await IsPageHiddenAsync())
        {
          continue;
        }
        await InvokeAsync(() => RefreshBusesAsync(force: false));
      }
    }
    catch (OperationCanceledException)
    {
      // Normal shutdown — timer was cancelled via CancellationTokenSource
    }
  }

  private async ValueTask<bool> IsPageHiddenAsync()
  {
    if (mapModule == null || _isDisposed) return false;
    try
    {
      return await mapModule.InvokeAsync<bool>("isPageHidden");
    }
    catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException or OperationCanceledException)
    {
      return false;
    }
  }

  private async Task InitializeMapOptimisticallyAsync(Task<Location?> locationTask)
  {
    if (mapModule is null || _isDisposed) return;

    try
    {
      Session.ResetAuthFailure();
      var apiKey = Config["GoogleMaps:ApiKey"];

      if (!locationTask.IsCompleted && LocationService.LastKnownLocation is null)
      {
        await Task.WhenAny(locationTask, Task.Delay(150));
        if (_isDisposed) return;
      }

      var resolvedEarly = locationTask.IsCompletedSuccessfully ? await locationTask : null;
      var initialLocation = resolvedEarly ?? LocationService.LastKnownLocation;
      var initialLat = initialLocation?.Latitude ?? Session.DefaultLatitude;
      var initialLng = initialLocation?.Longitude ?? Session.DefaultLongitude;

      var initMapTask = mapModule.InvokeVoidAsync("initGoogleMaps", "map", initialLat, initialLng, 14, apiKey, objRef,
        Theme.IsDarkMode ? "DARK" : "LIGHT", MalaysiaTime.IanaTimeZone).AsTask();

      if (resolvedEarly is null && !locationTask.IsCompleted)
      {
        Session.ReportGpsLoading();
        StateHasChanged();
      }

      var position = resolvedEarly ?? await locationTask;
      if (_isDisposed) return;

      if (position is not null)
      {
        await initMapTask;
        if (_isDisposed) return;

        var movedFromInitial = Math.Abs(position.Latitude - initialLat) > 1e-6
          || Math.Abs(position.Longitude - initialLng) > 1e-6;
        if (movedFromInitial)
        {
          await mapModule.InvokeVoidAsync("updateUserMarker", position.Latitude, position.Longitude);
        }

        Session.ReportLiveLocation();
        StateHasChanged();
        await Task.WhenAll(
          UpdateOriginFromLocation(position.Latitude, position.Longitude),
          RefreshBusesAsync()
        );
      }
      else
      {
        await initMapTask;
        if (_isDisposed) return;

        Session.ApplyFallbackLocation(initialLat, initialLng);
        await RefreshBusesAsync();
      }

      StateHasChanged();
    }
    catch (JSException ex)
    {
      Session.ReportMapError();
      Logger.LogWarning("initGoogleMaps failed: {Message}", ex.Message);
      if (!_isDisposed) StateHasChanged();
    }
  }

  private async Task UpdateOriginFromLocation(double lat, double lng)
  {
    if (_isDisposed || !string.IsNullOrEmpty(origin)) return;
    try
    {
      string? resolved = await PlaceResolution.ReverseGeocodeAsync(lat, lng);
      if (_isDisposed || string.IsNullOrEmpty(resolved)) return;
      origin = resolved;
      if (mapModule != null)
      {
        await mapModule.InvokeVoidAsync("setAutocompleteValue", "origin-input", origin, new LatLngCoordinates(lat, lng));
      }
    }
    catch (Exception ex)
    {
      if (_isDisposed) return;
      Logger.LogDebug("Reverse geocode failed: {Message}", ex.Message);
    }
  }

  [JSInvokable]
  public void OnMapAuthError(string message)
  {
    if (_isDisposed) return;
    Session.SetAuthError(message);
    StateHasChanged();
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
    var result = await Session.UpdateUserPositionAsync(lat, lng, _cts.Token);
    if (_isDisposed) return;
    if (result != null && mapModule != null && !Session.MapAuthFailed)
    {
      try
      {
        await mapModule.InvokeVoidAsync("syncMarkers", result.Vehicles);
      }
      catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or JSException) { }
    }
    if (!_isDisposed) StateHasChanged();
  }

  [JSInvokable]
  public void UpdateDestination(string value)
  {
    if (_isDisposed) return;
    destination = value;
    StateHasChanged();
  }

  [JSInvokable]
  public async Task OnRouteSelected(int routeId)
  {
    if (_isDisposed) return;
    if (Session.SetActiveRoute(routeId, announceRoute: true, filterVehicles: true))
    {
      await SyncMarkersFromCacheAsync();
      if (!_isDisposed) StateHasChanged();
    }
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
      if (!_isDisposed) Logger.LogDebug("Autocomplete init failed: {Message}", ex.Message);
    }
  }

  private async Task RequestManualLocationUpdate()
  {
    var position = await LocationService.GetCurrentLocationAsync(forceRefresh: true);
    if (_isDisposed || position == null || mapModule == null) return;

    try
    {
      await mapModule.InvokeVoidAsync("updateUserMarker", position.Latitude, position.Longitude);
      var resolved = await PlaceResolution.ReverseGeocodeAsync(position.Latitude, position.Longitude);
      if (!string.IsNullOrEmpty(resolved)) origin = resolved;

      if (_isDisposed || mapModule == null) return;
      await mapModule.InvokeVoidAsync("setAutocompleteValue", "origin-input", origin,
        new LatLngCoordinates(position.Latitude, position.Longitude));
      await RefreshBusesAsync();
      StateHasChanged();
    }
    catch (Exception ex)
    {
      if (_isDisposed) return;
      Logger.LogWarning("Manual location update failed: {Message}", ex.Message);
    }
  }

  private async Task ResetRoutesAndRefreshBusesAsync()
  {
    if (mapModule == null || _isDisposed || Session.MapAuthFailed) return;
    await ClearAllRoutesForRefreshAsync();
    await RefreshBusesAsync(force: true);
  }

  private async Task RefreshBusesAsync(bool force = true)
  {
    if (mapModule == null || _isDisposed || Session.MapAuthFailed) return;
    var previousStatus = Session.LocationStatus;
    var result = await Session.RefreshVehiclesAsync(force, _cts.Token);
    if (_isDisposed || result == null || mapModule == null || Session.MapAuthFailed) return;

    try
    {
      if (force || result.VehiclesChanged)
      {
        await mapModule.InvokeVoidAsync("syncMarkers", result.Vehicles);
      }
      if (!_isDisposed && (force || result.VehiclesChanged || !string.Equals(previousStatus, Session.LocationStatus, StringComparison.Ordinal)))
      {
        StateHasChanged();
      }
    }
    catch (OperationCanceledException) { }
    catch (ObjectDisposedException) { }
    catch (Exception ex)
    {
      if (!_isDisposed) Logger.LogWarning("syncMarkers failed: {Message}", ex.Message);
    }
  }

  private async Task SyncMarkersFromCacheAsync()
  {
    if (mapModule == null || _isDisposed || Session.MapAuthFailed) return;
    try
    {
      await mapModule.InvokeVoidAsync("syncMarkers", Session.GetDisplayedVehicles());
    }
    catch (OperationCanceledException) { }
    catch (ObjectDisposedException) { }
    catch (Exception ex)
    {
      if (!_isDisposed) Logger.LogWarning("syncMarkers failed: {Message}", ex.Message);
    }
  }

  private async Task ShowRoute()
  {
    if (mapModule == null || _isDisposed) return;

    var wasFiltered = Session.HasActiveVehicleFilter;
    if (!Session.TryBeginRouteCalculation(origin, destination))
    {
      StateHasChanged();
      return;
    }

    try
    {
      StateHasChanged();
      if (wasFiltered)
      {
        await SyncMarkersFromCacheAsync();
      }

      if (!string.Equals(mode, "TRANSIT", StringComparison.Ordinal))
      {
        var routeFound = await mapModule.InvokeAsync<bool>("showRouteByName", origin, destination, mode);
        Session.ApplyNonTransitRouteResult(mode, routeFound);
        StateHasChanged();
        return;
      }

      TransitRouteRequestOptions? routeOptions = string.Equals(departureMode, "now", StringComparison.Ordinal)
        ? null
        : new TransitRouteRequestOptions(
          departureMode,
          MalaysiaTime.At(departureDateTime).ToString("O"));

      var googleRoutes = await mapModule.InvokeAsync<List<GoogleRoute>>("getTransitRoutes", origin, destination, routeOptions);
      var options = await Session.EnrichTransitRoutesAsync(googleRoutes, _cts.Token);
      if (_isDisposed) return;

      if (options.Count > 0)
      {
        var routeData = options.Select(r => new RenderedRouteSummary(
          Id: r.OriginalRoute.RouteId,
          Path: r.OriginalRoute.EncodedPolyline,
          HasLiveBus: r.HasLiveBus,
          DurationSeconds: r.OriginalRoute.DurationSeconds,
          LocalizedDuration: r.OriginalRoute.LocalizedDuration,
          HeadwayMinutes: r.OriginalRoute.HeadwayMinutes)).ToList();

        await mapModule.InvokeVoidAsync("drawMultipleRoutes", routeData, Session.ActiveRouteId, objRef);
      }

      StateHasChanged();
    }
    catch (Exception ex)
    {
      if (_isDisposed) return;
      Session.ReportRouteFailure();
      Logger.LogWarning("Route display failed: {Message}", ex.Message);
      StateHasChanged();
    }
  }

  private async Task SelectRoute(EnrichedRoute route)
  {
    if (_isDisposed) return;
    Session.SetActiveRoute(route.OriginalRoute.RouteId, announceRoute: true, filterVehicles: true);
    isSearchPanelCollapsed = true;
    if (mapModule == null)
    {
      StateHasChanged();
      return;
    }

    try
    {
      await mapModule.InvokeVoidAsync("setActiveRoute", Session.ActiveRouteId);
      await SyncMarkersFromCacheAsync();
      if (!_isDisposed) StateHasChanged();
    }
    catch (Exception ex)
    {
      Logger.LogWarning("SelectRoute failed: {Message}", ex.Message);
      if (!_isDisposed) StateHasChanged();
    }
  }

  private async Task ClearRoute()
  {
    if (mapModule == null || _isDisposed) return;

    Session.ClearRoutes();
    isSearchPanelCollapsed = false;
    await TryInvokeMapCleanupAsync("clearRoute", "Clear route failed");
    if (_isDisposed) return;
    await SyncMarkersFromCacheAsync();
    if (!_isDisposed) StateHasChanged();
  }

  private async Task ClearAllRoutesForRefreshAsync()
  {
    if (mapModule == null || _isDisposed || Session.MapAuthFailed) return;

    var wasFiltered = Session.HasActiveVehicleFilter;
    _staticOverlaySeq++;
    Session.ClearAllRoutes(announce: false);
    isSearchPanelCollapsed = false;

    await TryInvokeMapCleanupAsync("clearStaticRoute", "Refresh buses static route reset failed");
    await TryInvokeMapCleanupAsync("clearRoute", "Refresh buses route reset failed");
    if (_isDisposed) return;
    if (wasFiltered)
    {
      await SyncMarkersFromCacheAsync();
    }
    if (!_isDisposed) StateHasChanged();
  }

  private async Task TryInvokeMapCleanupAsync(string identifier, string failureAction)
  {
    if (mapModule == null || _isDisposed) return;

    try
    {
      await mapModule.InvokeVoidAsync(identifier);
    }
    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or JSException)
    {
      if (!_isDisposed) Logger.LogWarning("{Action}: {Message}", failureAction, ex.Message);
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

    await _cts.CancelAsync();
    try
    {
      await Task.WhenAll(_autocompleteTask, _pollingTask);
    }
    catch (Exception)
    {
      // Normal during disposal or cancellation
    }
    _cts.Dispose();
    if (TryGetCachedFeatureService<TransitMapSession>(out var session))
    {
      session.Dispose();
    }

    if (mapModule != null)
    {
      var module = mapModule;
      mapModule = null;
      await module.TryInvokeVoidSafeAsync("cleanupMap");
      try
      {
        await module.DisposeAsync();
      }
      catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException or OperationCanceledException) { }
    }
  }
}
