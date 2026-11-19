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
  private int _refreshInProgress;
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
  public bool IsErrorStatus { get; private set; }

  public int SelectedStaticProviderIndex { get; private set; } = -1;
  public string SelectedStaticRouteId { get; private set; } = string.Empty;
  public List<StaticRoute> StaticRoutes { get; private set; } = [];
  public List<StaticDeparture> StaticDepartures { get; private set; } = [];
  public string SelectedStaticStopId { get; private set; } = string.Empty;
  public string StaticStopName { get; private set; } = string.Empty;
  public string StaticStatus { get; private set; } = string.Empty;
  public bool IsStaticErrorStatus { get; private set; }

  private void SetLocationStatus(string status, bool isError = false)
  {
    LocationStatus = status;
    IsErrorStatus = isError;
  }

  private void SetStaticStatus(string status, bool isError = false)
  {
    StaticStatus = status;
    IsStaticErrorStatus = isError;
  }

  public List<EnrichedRoute> RouteOptions { get; private set; } = [];
  public int ActiveRouteId { get; private set; } = -1;
  public bool IsRouteVehicleFilterActive { get; private set; }
  public bool IsStaticRouteVehicleFilterActive { get; private set; }
  public bool HasActiveVehicleFilter => IsRouteVehicleFilterActive || IsStaticRouteVehicleFilterActive;

  public IReadOnlyList<BusLocation> GetDisplayedVehicles() =>
    FilterDisplayedVehicles(_transitTracking.CachedVehicles);

  public TransportStaticData? SelectedStaticData =>
    SelectedStaticProviderIndex >= 0
    && SelectedStaticProviderIndex < Providers.Count
    && Providers[SelectedStaticProviderIndex].HasStaticData
      ? Providers[SelectedStaticProviderIndex].StaticData
      : null;

  public void ResetAuthFailure() => MapAuthFailed = false;

  public void ReportGpsLoading() => SetLocationStatus("Loading GPS...");

  public void ReportLiveLocation() => SetLocationStatus("Live");

  public void ReportMapError() => SetLocationStatus("Map error", isError: true);

  public void ReportRouteFailure() => SetLocationStatus("Route calculation failed.", isError: true);

  public void SetAuthError(string message)
  {
    MapAuthFailed = true;
    SetLocationStatus(message, isError: true);
  }

  public void ApplyFallbackLocation(double initialLat, double initialLng)
  {
    if (_locationService.LastKnownLocation is null)
    {
      _locationService.UpdateLocation(initialLat, initialLng);
      SetLocationStatus("Using default location");
    }
    else
    {
      SetLocationStatus("Using cached location");
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
    if (_disposed || pos == null || MapAuthFailed || cancellationToken.IsCancellationRequested) return null;

    if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0) return null;

    try
    {
      var result = await _transitTracking.RefreshVehiclesAsync(pos.Latitude, pos.Longitude, force, cancellationToken);
      if (_disposed || result is null) return null;
      if (!MapAuthFailed)
      {
        var hasFeedFailure = result.Vehicles.Count == 0 && (result.FailedProviderCount > 0 || result.ExpiredProviderCount > 0);
        SetLocationStatus(result.StatusMessage, hasFeedFailure);
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
      Interlocked.Exchange(ref _refreshInProgress, 0);
    }
  }

  public async Task<string?> SelectStaticProviderAsync(int index, CancellationToken cancellationToken = default)
  {
    var providerSeq = Interlocked.Increment(ref _staticProviderSeq);
    Interlocked.Increment(ref _staticRouteSeq);
    Interlocked.Increment(ref _staticStopSeq);

    SelectedStaticProviderIndex = index;
    SelectedStaticRouteId = string.Empty;
    IsStaticRouteVehicleFilterActive = false;
    StaticRoutes = [];
    StaticDepartures = [];
    SelectedStaticStopId = string.Empty;
    StaticStopName = string.Empty;

    var source = SelectedStaticData;
    if (source == null)
    {
      SetStaticStatus(string.Empty);
      return null;
    }

    SetStaticStatus("Loading routes...");
    try
    {
      var routes = await _staticData.GetRoutesAsync(source, cancellationToken);
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq)) return null;

      StaticRoutes = routes;
      SetStaticStatus(StaticRoutes.Count == 0
        ? "No routes available."
        : "Select a route to view its map.");
      return null;
    }
    catch (OperationCanceledException)
    {
      return null;
    }
    catch (Exception ex)
    {
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq)) return null;
      SetStaticStatus("Static routes unavailable.", isError: true);
      _logger.LogWarning("Static route list failed: {Message}", ex.Message);
      return null;
    }
  }

  public bool HasStaticDataForProvider(string? providerName) =>
    FindStaticProviderIndex(providerName) >= 0;

  public async Task<string?> SelectStaticRouteAsync(string routeId, CancellationToken cancellationToken = default)
  {
    var providerSeq = Volatile.Read(ref _staticProviderSeq);
    var routeSeq = Interlocked.Increment(ref _staticRouteSeq);
    Interlocked.Increment(ref _staticStopSeq);

    SelectedStaticRouteId = routeId;
    ClearStaticStopSelection();

    var source = SelectedStaticData;
    if (source == null)
    {
      IsStaticRouteVehicleFilterActive = false;
      SetStaticStatus(string.Empty);
      return null;
    }

    if (!string.IsNullOrWhiteSpace(routeId))
    {
      IsStaticRouteVehicleFilterActive = true;
      IsRouteVehicleFilterActive = false;
    }
    else
    {
      IsStaticRouteVehicleFilterActive = false;
    }

    return await LoadStaticRouteMapAsync(source, routeId, providerSeq, routeSeq, cancellationToken);
  }

  public async Task<string?> SelectVehicleRouteAsync(
    string? providerName,
    string? routeId,
    string? routeShortName = null,
    CancellationToken cancellationToken = default)
  {
    var providerIndex = FindStaticProviderIndex(providerName);
    if (providerIndex < 0) return null;

    var candidateId = routeId?.Trim();
    var candidateShortName = routeShortName?.Trim();
    if (string.IsNullOrEmpty(candidateId) && string.IsNullOrEmpty(candidateShortName)) return null;

    int providerSeq;
    int routeSeq;
    if (SelectedStaticProviderIndex != providerIndex || StaticRoutes.Count == 0)
    {
      providerSeq = Interlocked.Increment(ref _staticProviderSeq);
      routeSeq = Interlocked.Increment(ref _staticRouteSeq);
      Interlocked.Increment(ref _staticStopSeq);

      SelectedStaticProviderIndex = providerIndex;
      SelectedStaticRouteId = string.Empty;
      IsStaticRouteVehicleFilterActive = false;
      StaticRoutes = [];
      ClearStaticStopSelection();
    }
    else
    {
      providerSeq = Volatile.Read(ref _staticProviderSeq);
      routeSeq = Interlocked.Increment(ref _staticRouteSeq);
      Interlocked.Increment(ref _staticStopSeq);

      ClearStaticStopSelection();
    }

    var source = SelectedStaticData;
    if (source == null)
    {
      IsStaticRouteVehicleFilterActive = false;
      SetStaticStatus(string.Empty);
      return null;
    }

    SetStaticStatus("Loading route map...");
    try
    {
      if (StaticRoutes.Count == 0)
      {
        var routes = await _staticData.GetRoutesAsync(source, cancellationToken);
        if (IsStaleStaticRouteSelection(providerSeq, routeSeq)) return null;
        StaticRoutes = routes;
      }

      var resolvedRouteId = ResolveStaticRouteId(StaticRoutes, candidateId, candidateShortName);
      if (string.IsNullOrEmpty(resolvedRouteId))
      {
        SelectedStaticRouteId = string.Empty;
        IsStaticRouteVehicleFilterActive = false;
        SetStaticStatus("Static route map unavailable.", isError: true);
        return null;
      }

      SelectedStaticRouteId = resolvedRouteId;
      IsStaticRouteVehicleFilterActive = true;
      IsRouteVehicleFilterActive = false;
      return await LoadStaticRouteMapAsync(source, resolvedRouteId, providerSeq, routeSeq, cancellationToken);
    }
    catch (OperationCanceledException)
    {
      return null;
    }
    catch (Exception ex)
    {
      if (IsStaleStaticRouteSelection(providerSeq, routeSeq)) return null;
      SetStaticStatus("Static route map unavailable.", isError: true);
      _logger.LogWarning("Static route map failed: {Message}", ex.Message);
      return null;
    }
  }

  public bool ClearVehicleRouteSelection(string? providerName = null)
  {
    if (_disposed) return false;

    if (!string.IsNullOrWhiteSpace(providerName))
    {
      var providerIndex = FindStaticProviderIndex(providerName);
      if (providerIndex < 0) return false;
      if (SelectedStaticProviderIndex >= 0 && SelectedStaticProviderIndex != providerIndex) return false;
    }

    Interlocked.Increment(ref _staticRouteSeq);
    Interlocked.Increment(ref _staticStopSeq);

    SelectedStaticRouteId = string.Empty;
    IsStaticRouteVehicleFilterActive = false;
    ClearStaticStopSelection();

    if (SelectedStaticData == null)
    {
      SetStaticStatus(string.Empty);
      return true;
    }

    if (StaticRoutes.Count == 0)
    {
      Interlocked.Increment(ref _staticProviderSeq);
      SelectedStaticProviderIndex = -1;
      SetStaticStatus(string.Empty);
      return true;
    }

    SetStaticStatus("Select a route to view its map.");
    return true;
  }

  private async Task<string?> LoadStaticRouteMapAsync(
    TransportStaticData source,
    string routeId,
    int providerSeq,
    int routeSeq,
    CancellationToken cancellationToken)
  {
    SetStaticStatus("Loading route map...");
    try
    {
      var geoJson = await _staticData.GetMapAsync(source, routeId, cancellationToken);
      if (IsStaleStaticRouteSelection(providerSeq, routeSeq)) return null;
      if (string.IsNullOrWhiteSpace(geoJson))
      {
        SetStaticStatus("Static route map unavailable.", isError: true);
        return null;
      }
      SetStaticStatus("Select a stop on the map for departures.");
      return geoJson;
    }
    catch (OperationCanceledException)
    {
      return null;
    }
    catch (Exception ex)
    {
      if (IsStaleStaticRouteSelection(providerSeq, routeSeq)) return null;
      SetStaticStatus("Static route map unavailable.", isError: true);
      _logger.LogWarning("Static route map failed: {Message}", ex.Message);
      return null;
    }
  }

  private int FindStaticProviderIndex(string? providerName)
  {
    if (_disposed || string.IsNullOrWhiteSpace(providerName)) return -1;
    var trimmed = providerName.Trim();
    for (var i = 0; i < Providers.Count; i++)
    {
      if (Providers[i].HasStaticData &&
          string.Equals(Providers[i].Name, trimmed, StringComparison.OrdinalIgnoreCase))
      {
        return i;
      }
    }
    return -1;
  }

  private static string? ResolveStaticRouteId(
    IReadOnlyList<StaticRoute> routes,
    string? routeId,
    string? routeShortName)
  {
    if (!string.IsNullOrEmpty(routeId))
    {
      var byId = routes.FirstOrDefault(r => string.Equals(r.Id?.Trim(), routeId, StringComparison.OrdinalIgnoreCase));
      if (byId != null) return byId.Id;

      var byName = routes.FirstOrDefault(r => string.Equals(r.Name?.Trim(), routeId, StringComparison.OrdinalIgnoreCase));
      if (byName != null) return byName.Id;
    }

    if (!string.IsNullOrEmpty(routeShortName))
    {
      var byShortName = routes.FirstOrDefault(r => string.Equals(r.Name?.Trim(), routeShortName, StringComparison.OrdinalIgnoreCase));
      if (byShortName != null) return byShortName.Id;
    }

    return !string.IsNullOrEmpty(routeId) ? routeId : null;
  }

  private void ClearStaticStopSelection()
  {
    StaticDepartures = [];
    SelectedStaticStopId = string.Empty;
    StaticStopName = string.Empty;
  }

  private bool IsStaleStaticRouteSelection(int providerSeq, int routeSeq) =>
    _disposed || providerSeq != Volatile.Read(ref _staticProviderSeq) || routeSeq != Volatile.Read(ref _staticRouteSeq);

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
    SetStaticStatus("Loading estimated departures...");

    try
    {
      var departures = await _staticData.GetDeparturesAsync(source, routeId, stopId, cancellationToken);
      if (_disposed || providerSeq != Volatile.Read(ref _staticProviderSeq) || routeSeq != Volatile.Read(ref _staticRouteSeq) || stopSeq != Volatile.Read(ref _staticStopSeq))
        return false;

      StaticDepartures = departures;
      SetStaticStatus(departures.Count == 0
        ? "No upcoming departures for this route."
        : "Scheduled estimates, not live arrivals.");
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

      SetStaticStatus("Departures unavailable.", isError: true);
      _logger.LogWarning("Static departures failed: {Message}", ex.Message);
      return true;
    }
  }

  public async Task<IReadOnlyList<EnrichedRoute>> EnrichTransitRoutesAsync(List<GoogleRoute>? googleRoutes, CancellationToken cancellationToken = default)
  {
    RouteOptions = [];
    if (googleRoutes == null || googleRoutes.Count == 0)
    {
      SetLocationStatus("No transit routes found.");
      return RouteOptions;
    }

    RouteOptions = await _transitTracking.EnrichRoutesAsync(googleRoutes, cancellationToken);
    if (RouteOptions.Count > 0)
    {
      ActiveRouteId = RouteOptions[0].OriginalRoute.RouteId;
    }
    SetLocationStatus($"Found {RouteOptions.Count} routes.");
    return RouteOptions;
  }

  public bool TryBeginRouteCalculation(string? origin, string? destination)
  {
    if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination))
    {
      SetLocationStatus("Please enter origin and destination.");
      return false;
    }

    SetLocationStatus("Calculating live routes...");
    RouteOptions = [];
    ActiveRouteId = -1;
    IsRouteVehicleFilterActive = false;
    IsStaticRouteVehicleFilterActive = false;
    return true;
  }

  public void ApplyNonTransitRouteResult(string mode, bool routeFound)
  {
    ActiveRouteId = -1;
    IsRouteVehicleFilterActive = false;
    IsStaticRouteVehicleFilterActive = false;
    var trimmedMode = string.IsNullOrWhiteSpace(mode) ? "Route" : mode.Trim();
    SetLocationStatus(routeFound
      ? $"{char.ToUpperInvariant(trimmedMode[0])}{trimmedMode[1..].ToLowerInvariant()} route ready."
      : $"No {trimmedMode.ToLowerInvariant()} routes found.");
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
      IsStaticRouteVehicleFilterActive = false;
    }
    if (announceRoute)
    {
      SetLocationStatus($"Showing Route {routeId + 1}");
    }
    return true;
  }

  public void ClearRoutes(bool announce = true)
  {
    RouteOptions = [];
    ActiveRouteId = -1;
    IsRouteVehicleFilterActive = false;
    IsStaticRouteVehicleFilterActive = false;
    if (announce)
    {
      SetLocationStatus("Route cleared.");
    }
  }

  public void ClearAllRoutes(bool announce = true)
  {
    Interlocked.Increment(ref _staticProviderSeq);
    Interlocked.Increment(ref _staticRouteSeq);
    Interlocked.Increment(ref _staticStopSeq);

    SelectedStaticProviderIndex = -1;
    SelectedStaticRouteId = string.Empty;
    StaticRoutes = [];
    ClearStaticStopSelection();
    SetStaticStatus(string.Empty);

    ClearRoutes(announce);
  }

  private IReadOnlyList<BusLocation> FilterDisplayedVehicles(IReadOnlyList<BusLocation> vehicles)
  {
    if (IsRouteVehicleFilterActive)
    {
      var activeRoute = RouteOptions.FirstOrDefault(r => r.OriginalRoute.RouteId == ActiveRouteId);
      return activeRoute is null
        ? vehicles
        : LiveRouting.FilterVehiclesForRoute(activeRoute.OriginalRoute, vehicles);
    }

    if (IsStaticRouteVehicleFilterActive &&
        SelectedStaticProviderIndex >= 0 &&
        SelectedStaticProviderIndex < Providers.Count &&
        !string.IsNullOrWhiteSpace(SelectedStaticRouteId))
    {
      return LiveRouting.FilterVehiclesForStaticRoute(
        Providers[SelectedStaticProviderIndex].Name,
        SelectedStaticRouteId,
        StaticRoutes,
        vehicles);
    }

    return vehicles;
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    StaticRoutes = [];
    StaticDepartures = [];
    RouteOptions = [];
  }
}
