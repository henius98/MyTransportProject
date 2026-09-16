
namespace MyTransportAppWASM.Pages.Map;

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
  private IReadOnlyList<TransportProvider> _transportProviders = Array.Empty<TransportProvider>();
  private List<MyTransportAppWASM.Models.BusLocation> _cachedVehicles = [];
  private TimeSpan _pollingInterval = TimeSpan.FromSeconds(15);
  private TransitRefreshCoordinator _transitRefresh = new(
    TimeSpan.FromSeconds(15),
    TimeSpan.FromMinutes(2),
    TimeSpan.FromMinutes(2));
  private readonly SemaphoreSlim _refreshGate = new(1, 1);
  private List<MyTransportAppWASM.Models.EnrichedRoute> _routeOptions = new();
  private int _activeRouteId = -1;
  private Task _autocompleteTask = Task.CompletedTask;
  private Task _pollingTask = Task.CompletedTask;
  private Task _fortuneTask = Task.CompletedTask;

  private string MapContainerClass => Theme.IsDarkMode ? "theme-dark" : "theme-light";
  private string MapOverlayClass => $"map-overlay {(isCollapsed ? "collapsed" : "")}".Trim();

  private void ToggleCollapse() => isCollapsed = !isCollapsed;

  protected override Task OnInitializedAsync()
  {
    Latitude = Config.GetValue<double?>("DefaultLocation:Latitude")
        ?? throw new InvalidOperationException("Missing configuration: DefaultLocation:Latitude");
    Longitude = Config.GetValue<double?>("DefaultLocation:Longitude")
        ?? throw new InvalidOperationException("Missing configuration: DefaultLocation:Longitude");
    _transportProviders = Config.GetSection("TransportProviders").Get<List<TransportProvider>>() ?? [];

    var pollingSeconds = Math.Max(5, Config.GetValue("TransitPolling:IntervalSeconds", 15));
    var maximumBackoffSeconds = Math.Max(
      pollingSeconds,
      Config.GetValue("TransitPolling:MaximumBackoffSeconds", 120));
    var maximumSnapshotAgeSeconds = Math.Max(
      pollingSeconds,
      Config.GetValue("TransitPolling:MaximumSnapshotAgeSeconds", 120));
    _pollingInterval = TimeSpan.FromSeconds(pollingSeconds);
    _transitRefresh = new TransitRefreshCoordinator(
      _pollingInterval,
      TimeSpan.FromSeconds(maximumBackoffSeconds),
      TimeSpan.FromSeconds(maximumSnapshotAgeSeconds));

    Theme.OnThemeChanged += OnThemeChanged;

    // Fortune data is optional route decoration and must not delay the map's first render.
    _fortuneTask = LoadTodayFortuneAsync();
    return Task.CompletedTask;
  }

  private async Task LoadTodayFortuneAsync()
  {
    try
    {
      if (!await BaziFlowService.HasApiKeyAsync())
      {
        return;
      }

      TodayFortune = await BaziFlowService.GetDateFortuneAsync(DateTime.Now.ToString("yyyy-MM-dd"));
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Today's fortune could not be loaded: {ex.Message}");
    }
  }

  private void OnThemeChanged() => _ = InvokeAsync(UpdateMapThemeAsync);

  private async Task UpdateMapThemeAsync()
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
    LocationService.UpdateLocation(lat, lng);
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

  private Task RefreshBusPosition() => RefreshBusPosition(force: true);

  private async Task RefreshBusPosition(bool force)
  {
    var pos = LocationService.LastKnownLocation;
    if (mapModule == null || _isDisposed || pos == null) return;
    if (!await _refreshGate.WaitAsync(0)) return;

    try
    {
      var nearbyProviders = _transportProviders.Where(p =>
          CalculateDistance(pos.Latitude, pos.Longitude, p.CenterLat, p.CenterLng) <= p.RadiusKm
      ).ToList();

      if (nearbyProviders.Count == 0)
      {
        _transitRefresh.Clear();
        _cachedVehicles = [];
        await mapModule.InvokeVoidAsync("syncMarkers", Array.Empty<MyTransportAppWASM.Models.BusLocation>());
        locationStatus = "No bus providers found nearby.";
        StateHasChanged();
        return;
      }

      if (_isDisposed) return;
      var cancellationToken = _cts.Token;
      var refreshStartedAt = DateTimeOffset.UtcNow;
      var providersToRefresh = _transitRefresh.GetProvidersToRefresh(
        nearbyProviders,
        refreshStartedAt,
        force);

      var refreshResults = await Task.WhenAll(
        providersToRefresh.Select(provider => FetchProviderVehiclesAsync(
          provider,
          _transitRefresh.HasUsableSnapshot(provider.Endpoint, refreshStartedAt),
          cancellationToken)));

      if (_isDisposed || mapModule == null) return;

      var outcome = _transitRefresh.ApplyResults(
        nearbyProviders,
        refreshResults,
        DateTimeOffset.UtcNow);
      _cachedVehicles = outcome.Vehicles;

      await mapModule.InvokeVoidAsync("syncMarkers", _cachedVehicles);

      var successfulRefreshCount = outcome.AttemptedProviderCount - outcome.FailedProviderCount;
      if (successfulRefreshCount > 0)
      {
        lastUpdated = DateTime.Now;
      }

      locationStatus = BuildTransitStatus(outcome, nearbyProviders.Count);
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
    finally
    {
      _refreshGate.Release();
    }
  }

  private async Task<ProviderVehicleRefresh> FetchProviderVehiclesAsync(
    TransportProvider provider,
    bool allowNotModified,
    CancellationToken cancellationToken)
  {
    var result = await GtfsService.GetBusPositionsAsync(
      provider.Endpoint,
      allowNotModified,
      cancellationToken);
    if (_isDisposed || result.Status == GtfsFetchStatus.Failed)
    {
      return new ProviderVehicleRefresh(provider.Endpoint, ProviderVehicleRefreshStatus.Failed);
    }

    if (result.Status == GtfsFetchStatus.NotModified)
    {
      return new ProviderVehicleRefresh(provider.Endpoint, ProviderVehicleRefreshStatus.NotModified);
    }

    if (result.Vehicles is not { } vehicles)
    {
      return new ProviderVehicleRefresh(provider.Endpoint, ProviderVehicleRefreshStatus.Failed);
    }

    foreach (var vehicle in vehicles)
    {
      vehicle.TransportType = provider.TransportType;
    }

    return new ProviderVehicleRefresh(
      provider.Endpoint,
      ProviderVehicleRefreshStatus.Updated,
      vehicles);
  }

  private string BuildTransitStatus(TransitRefreshOutcome outcome, int nearbyProviderCount)
  {
    if (_cachedVehicles.Count == 0)
    {
      return outcome.FailedProviderCount > 0 || outcome.ExpiredProviderCount > 0
        ? "Live bus data unavailable; retrying with backoff."
        : $"No active vehicles reported ({nearbyProviderCount} source(s)).";
    }

    var staleSuffix = outcome.StaleProviderCount > 0
      ? $", {outcome.StaleProviderCount} temporarily stale"
      : string.Empty;
    var expiredSuffix = outcome.ExpiredProviderCount > 0
      ? $", {outcome.ExpiredProviderCount} expired"
      : string.Empty;

    if (lastUpdated == default)
    {
      return $"Showing {_cachedVehicles.Count} buses ({nearbyProviderCount} source(s){staleSuffix}{expiredSuffix})";
    }

    return $"Buses updated: {lastUpdated:HH:mm:ss} ({nearbyProviderCount} source(s){staleSuffix}{expiredSuffix})";
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

      _routeOptions = await LiveRouting.EnrichRoutesAsync(googleRoutes, _cachedVehicles);

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

    Theme.OnThemeChanged -= OnThemeChanged;

    // Cancel the polling loop and any in-flight HTTP calls
    await _cts.CancelAsync();
    await Task.WhenAll(_autocompleteTask, _pollingTask);
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
