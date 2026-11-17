using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Features.Transit.Services;

public sealed class TransitMapSession : IDisposable
{
  private readonly ITransitTrackingService _transitTracking;
  private readonly ITransportStaticDataService _staticData;
  private readonly ILocationService _locationService;
  private readonly ILogger<TransitMapSession> _logger;
  private readonly SemaphoreSlim _refreshGate = new(1, 1);
  private int _staticProviderSeq;
  private int _staticRouteSeq;
  private int _staticStopSeq;
  private bool _disposed;

  public TransitMapSession(
    ITransitTrackingService transitTracking,
    ITransportStaticDataService staticData,
    ILocationService locationService,
    IOptions<TransitOptions> transitOptions,
    IOptions<DefaultLocationOptions> defaultLocationOptions,
    ILogger<TransitMapSession>? logger = null)
  {
    _transitTracking = transitTracking;
    _staticData = staticData;
    _locationService = locationService;
    _logger = logger ?? NullLogger<TransitMapSession>.Instance;

    (DefaultLatitude, DefaultLongitude) = defaultLocationOptions.Value.ResolveCoordinates();

    var options = transitOptions.Value;
    Providers = options.Providers;
    PollingInterval = TimeSpan.FromSeconds(Math.Max(5, options.Polling.IntervalSeconds));
  }

  public double DefaultLatitude { get; }
  public double DefaultLongitude { get; }
  public IReadOnlyList<TransportProvider> Providers { get; }
  public TimeSpan PollingInterval { get; }

  public string LocationStatus { get; private set; } = "Initializing...";
  public bool MapAuthFailed { get; private set; }
  public bool IsErrorStatus =>
    !string.IsNullOrEmpty(LocationStatus) &&
    (LocationStatus.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
     LocationStatus.Contains("error", StringComparison.OrdinalIgnoreCase) ||
     LocationStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase));

  public int SelectedStaticProviderIndex { get; private set; } = -1;
  public string SelectedStaticRouteId { get; private set; } = string.Empty;
  public List<StaticRoute> StaticRoutes { get; private set; } = [];
  public List<StaticDeparture> StaticDepartures { get; private set; } = [];
  public string SelectedStaticStopId { get; private set; } = string.Empty;
  public string StaticStopName { get; private set; } = string.Empty;
  public string StaticStatus { get; private set; } = string.Empty;

  public List<EnrichedRoute> RouteOptions { get; private set; } = [];
  public int ActiveRouteId { get; private set; } = -1;
  public bool IsRouteVehicleFilterActive { get; private set; }

  public IReadOnlyList<BusLocation> GetDisplayedVehicles() =>
    FilterDisplayedVehicles(_transitTracking.CachedVehicles);

  public TransportStaticData? SelectedStaticData =>
    SelectedStaticProviderIndex >= 0
    && SelectedStaticProviderIndex < Providers.Count
    && Providers[SelectedStaticProviderIndex].HasStaticData
      ? Providers[SelectedStaticProviderIndex].StaticData
      : null;

  public void ResetAuthFailure() => MapAuthFailed = false;

  public void ReportGpsLoading() => LocationStatus = "Loading GPS...";

  public void ReportLiveLocation() => LocationStatus = "Live";

  public void ReportMapError() => LocationStatus = "Map error";

  public void ReportRouteFailure() => LocationStatus = "Route calculation failed.";

  public void SetAuthError(string message)
  {
    MapAuthFailed = true;
    LocationStatus = message;
  }

  public void ApplyFallbackLocation(double initialLat, double initialLng)
  {
    if (_locationService.LastKnownLocation is null)
    {
      _locationService.UpdateLocation(initialLat, initialLng);
      LocationStatus = "Using default location";
    }
    else
    {
      LocationStatus = "Using cached location";
    }
  }

  public Task<TransitTrackingResult?> UpdateUserPositionAsync(double lat, double lng, CancellationToken cancellationToken = default)
  {
    _locationService.UpdateLocation(lat, lng);
    return RefreshVehiclesAsync(force: true, cancellationToken);
  }

  public async Task<TransitTrackingResult?> RefreshVehiclesAsync(bool force, CancellationToken cancellationToken = default)
  {
    var pos = _locationService.LastKnownLocation;
    if (_disposed || pos == null || MapAuthFailed) return null;

    try
    {
      if (!await _refreshGate.WaitAsync(0, cancellationToken)) return null;
    }
    catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
    {
      return null;
    }

    try
    {
      var result = await _transitTracking.RefreshVehiclesAsync(pos.Latitude, pos.Longitude, force, cancellationToken);
      if (result is null) return null;
      if (!MapAuthFailed)
      {
        LocationStatus = result.StatusMessage;
      }
      return result with { Vehicles = FilterDisplayedVehicles(result.Vehicles) };
    }
    catch (OperationCanceledException)
    {
      return null;
    }
    catch (ObjectDisposedException)
    {
      return null;
    }
    catch (Exception ex)
    {
      _logger.LogWarning("RefreshBusPosition failed: {Message}", ex.Message);
      return null;
    }
    finally
    {
      try
      {
        if (!_disposed) _refreshGate.Release();
      }
      catch (ObjectDisposedException) { }
    }
  }

  public async Task<string?> SelectStaticProviderAsync(int index, CancellationToken cancellationToken = default)
  {
    var providerSeq = Interlocked.Increment(ref _staticProviderSeq);
    Interlocked.Increment(ref _staticRouteSeq);
    Interlocked.Increment(ref _staticStopSeq);

    SelectedStaticProviderIndex = index;
    SelectedStaticRouteId = string.Empty;
    StaticRoutes = [];
    StaticDepartures = [];
    SelectedStaticStopId = string.Empty;
    StaticStopName = string.Empty;

    var source = SelectedStaticData;
    if (source == null)
    {
      StaticStatus = string.Empty;
      return null;
    }

    StaticStatus = "Loading routes...";
    try
    {
      var routesTask = _staticData.GetRoutesAsync(source, cancellationToken);
      var mapTask = _staticData.GetMapAsync(source, "", cancellationToken);
      await Task.WhenAll(routesTask, mapTask);
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq)) return null;

      StaticRoutes = await routesTask;
      StaticStatus = StaticRoutes.Count == 0
        ? "No routes available."
        : "Select a stop on the map for departures.";
      return string.IsNullOrEmpty(SelectedStaticRouteId) ? await mapTask : null;
    }
    catch (OperationCanceledException)
    {
      return null;
    }
    catch (Exception ex)
    {
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq)) return null;
      StaticStatus = "Static routes unavailable.";
      _logger.LogWarning("Static route list failed: {Message}", ex.Message);
      return null;
    }
  }

  public async Task<string?> SelectStaticRouteAsync(string routeId, CancellationToken cancellationToken = default)
  {
    var providerSeq = Volatile.Read(ref _staticProviderSeq);
    var routeSeq = Interlocked.Increment(ref _staticRouteSeq);
    Interlocked.Increment(ref _staticStopSeq);

    SelectedStaticRouteId = routeId;
    StaticDepartures = [];
    SelectedStaticStopId = string.Empty;
    StaticStopName = string.Empty;

    var source = SelectedStaticData;
    if (source == null)
    {
      StaticStatus = string.Empty;
      return null;
    }

    StaticStatus = "Loading route map...";
    try
    {
      var geoJson = await _staticData.GetMapAsync(source, routeId, cancellationToken);
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq) || routeSeq != Volatile.Read(ref _staticRouteSeq)) return null;
      StaticStatus = "Select a stop on the map for departures.";
      return geoJson;
    }
    catch (OperationCanceledException)
    {
      return null;
    }
    catch (Exception ex)
    {
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq) || routeSeq != Volatile.Read(ref _staticRouteSeq)) return null;
      StaticStatus = "Static route map unavailable.";
      _logger.LogWarning("Static route map failed: {Message}", ex.Message);
      return null;
    }
  }

  public async Task<bool> LoadStaticDeparturesAsync(string stopId, string stopName, CancellationToken cancellationToken = default)
  {
    var source = SelectedStaticData;
    var routeId = SelectedStaticRouteId;
    if (_disposed || source == null) return false;

    var providerSeq = Volatile.Read(ref _staticProviderSeq);
    var routeSeq = Volatile.Read(ref _staticRouteSeq);
    var stopSeq = Interlocked.Increment(ref _staticStopSeq);

    SelectedStaticStopId = stopId;
    StaticStopName = string.IsNullOrWhiteSpace(stopName) ? stopId : stopName;
    StaticDepartures = [];
    StaticStatus = "Loading estimated departures...";

    try
    {
      var departures = await _staticData.GetDeparturesAsync(source, routeId, stopId, cancellationToken);
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq) || routeSeq != Volatile.Read(ref _staticRouteSeq) || stopSeq != Volatile.Read(ref _staticStopSeq))
        return false;

      StaticDepartures = departures;
      StaticStatus = departures.Count == 0
        ? "No upcoming departures for this route."
        : "Scheduled estimates, not live arrivals.";
      return true;
    }
    catch (OperationCanceledException)
    {
      return false;
    }
    catch (ObjectDisposedException)
    {
      return false;
    }
    catch (Exception ex)
    {
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq) || routeSeq != Volatile.Read(ref _staticRouteSeq) || stopSeq != Volatile.Read(ref _staticStopSeq))
        return false;

      StaticStatus = "Departures unavailable.";
      _logger.LogWarning("Static departures failed: {Message}", ex.Message);
      return true;
    }
  }

  public async Task<IReadOnlyList<EnrichedRoute>> EnrichTransitRoutesAsync(List<GoogleRoute>? googleRoutes, CancellationToken cancellationToken = default)
  {
    RouteOptions = [];
    if (googleRoutes == null || googleRoutes.Count == 0)
    {
      LocationStatus = "No transit routes found.";
      return RouteOptions;
    }

    RouteOptions = await _transitTracking.EnrichRoutesAsync(googleRoutes, cancellationToken);
    if (RouteOptions.Count > 0)
    {
      ActiveRouteId = RouteOptions[0].OriginalRoute.RouteId;
    }
    LocationStatus = $"Found {RouteOptions.Count} routes.";
    return RouteOptions;
  }

  public bool TryBeginRouteCalculation(string? origin, string? destination)
  {
    if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination))
    {
      LocationStatus = "Please enter origin and destination.";
      return false;
    }

    LocationStatus = "Calculating live routes...";
    RouteOptions = [];
    ActiveRouteId = -1;
    IsRouteVehicleFilterActive = false;
    return true;
  }

  public void ApplyNonTransitRouteResult(string mode, bool routeFound)
  {
    ActiveRouteId = -1;
    IsRouteVehicleFilterActive = false;
    var trimmedMode = string.IsNullOrWhiteSpace(mode) ? "Route" : mode.Trim();
    LocationStatus = routeFound
      ? $"{char.ToUpperInvariant(trimmedMode[0])}{trimmedMode[1..].ToLowerInvariant()} route ready."
      : $"No {trimmedMode.ToLowerInvariant()} routes found.";
  }

  public bool SetActiveRoute(int routeId, bool announceRoute = false, bool filterVehicles = false)
  {
    var routeChanged = ActiveRouteId != routeId;
    var filterChanged = filterVehicles && !IsRouteVehicleFilterActive;
    if (!routeChanged && !announceRoute && !filterChanged) return false;

    ActiveRouteId = routeId;
    if (filterVehicles)
    {
      IsRouteVehicleFilterActive = true;
    }
    if (announceRoute)
    {
      LocationStatus = $"Showing Route {routeId + 1}";
    }
    return true;
  }

  public void ClearRoutes()
  {
    RouteOptions = [];
    ActiveRouteId = -1;
    IsRouteVehicleFilterActive = false;
    LocationStatus = "Route cleared.";
  }

  private IReadOnlyList<BusLocation> FilterDisplayedVehicles(IReadOnlyList<BusLocation> vehicles)
  {
    if (!IsRouteVehicleFilterActive) return vehicles;

    var activeRoute = RouteOptions.FirstOrDefault(r => r.OriginalRoute.RouteId == ActiveRouteId);
    if (activeRoute is null) return vehicles;

    return LiveRouting.FilterVehiclesForRoute(activeRoute.OriginalRoute, vehicles);
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    _refreshGate.Dispose();
  }
}
