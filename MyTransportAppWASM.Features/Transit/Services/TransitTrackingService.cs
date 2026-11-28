using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Features.Transit.Services;

public sealed class TransitTrackingService : ITransitTrackingService, IDisposable
{
  private readonly IGtfsService _gtfsService;
  private readonly ITransportStaticDataService? _staticDataService;
  private readonly IReadOnlyList<TransportProvider> _transportProviders;
  private readonly TransitRefreshCoordinator _transitRefresh;
  private readonly SemaphoreSlim _refreshLock = new(1, 1);
  private readonly ILogger<TransitTrackingService> _logger;
  private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _providerRouteMaps = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, DateTimeOffset> _staticRouteRetryAfter = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, IReadOnlyList<StaticFrequency>> _providerFrequencies = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, DateTimeOffset> _providerFrequenciesRetryAfter = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, ScheduledRailDayNetwork> _scheduledDayNetworks = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, DateTimeOffset> _scheduledNetworkRetryAfter = new(StringComparer.OrdinalIgnoreCase);
  private readonly Func<DateTimeOffset> _utcNowProvider;
  private readonly MapCacheExpirationOptions _mapCacheExpiration;
  private DateTime? _staticCacheServiceDay;
  private bool _hasEstimatedVehicles;
  private bool _disposed;
  private List<BusLocation> _cachedVehicles = [];
  private DateTimeOffset? _lastUpdated;

  public IReadOnlyList<BusLocation> CachedVehicles => _cachedVehicles;

  [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
  public TransitTrackingService(
    IGtfsService gtfsService,
    IOptions<TransitOptions> transitOptions,
    ILogger<TransitTrackingService>? logger = null,
    ITransportStaticDataService? staticDataService = null,
    IOptions<CacheExpirationOptions>? cacheExpiration = null)
    : this(
        gtfsService,
        transitOptions.Value.Providers,
        transitOptions.Value.Polling.IntervalSeconds,
        transitOptions.Value.Polling.MaximumBackoffSeconds,
        transitOptions.Value.Polling.MaximumSnapshotAgeSeconds,
        logger,
        staticDataService,
        cacheExpiration: cacheExpiration)
  {
  }

  public TransitTrackingService(
    IGtfsService gtfsService,
    IReadOnlyList<TransportProvider> transportProviders,
    int intervalSeconds = 15,
    int maximumBackoffSeconds = 120,
    int maximumSnapshotAgeSeconds = 120,
    ILogger<TransitTrackingService>? logger = null,
    ITransportStaticDataService? staticDataService = null,
    Func<DateTimeOffset>? utcNowProvider = null,
    IOptions<CacheExpirationOptions>? cacheExpiration = null)
  {
    _gtfsService = gtfsService;
    _staticDataService = staticDataService;
    _transportProviders = transportProviders;
    _logger = logger ?? NullLogger<TransitTrackingService>.Instance;
    _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    _mapCacheExpiration = cacheExpiration?.Value.Map ?? new MapCacheExpirationOptions();
    var normalInterval = TimeSpan.FromSeconds(Math.Max(5, intervalSeconds));
    var maxBackoff = TimeSpan.FromSeconds(Math.Max(intervalSeconds, maximumBackoffSeconds));
    var maxSnapshotAge = TimeSpan.FromSeconds(Math.Max(intervalSeconds, maximumSnapshotAgeSeconds));
    _transitRefresh = new TransitRefreshCoordinator(normalInterval, maxBackoff, maxSnapshotAge);
  }

  public async Task<TransitTrackingResult> RefreshVehiclesAsync(
    double latitude,
    double longitude,
    bool force = false,
    CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    await _refreshLock.WaitAsync(cancellationToken);
    try
    {
      var nearbyRealtimeProviders = _transportProviders.Where(p =>
        p.HasRealtimeFeed &&
        GeoMath.DistanceKilometers(latitude, longitude, p.CenterLat, p.CenterLng) <= p.RadiusKm
      ).ToList();

      var nearbyScheduleProviders = _staticDataService == null
        ? []
        : _transportProviders.Where(p =>
            !p.HasRealtimeFeed &&
            p.HasFrequencyFeed &&
            GeoMath.DistanceKilometers(latitude, longitude, p.CenterLat, p.CenterLng) <= p.RadiusKm
          ).ToList();

      var totalNearbyProviderCount = nearbyRealtimeProviders.Count + nearbyScheduleProviders.Count;

      if (totalNearbyProviderCount == 0)
      {
        var hadVehicles = _cachedVehicles.Count > 0;
        _transitRefresh.Clear();
        _cachedVehicles = [];
        _hasEstimatedVehicles = false;
        return new TransitTrackingResult
        {
          Vehicles = _cachedVehicles,
          StatusMessage = "No live vehicle feeds nearby. Scheduled routes are available below.",
          LastUpdated = _lastUpdated,
          HasNearbyFeeds = false,
          NearbyProviderCount = 0,
          VehiclesChanged = hadVehicles
        };
      }

      var refreshStartedAt = _utcNowProvider();
      ExpireDerivedStaticDataIfReset(refreshStartedAt);
      var providersToRefresh = nearbyRealtimeProviders.Count > 0
        ? _transitRefresh.GetProvidersToRefresh(nearbyRealtimeProviders, refreshStartedAt, force)
        : [];

      var vehicleRefreshTask = providersToRefresh.Count > 0
        ? Task.WhenAll(
            providersToRefresh.Select(provider => FetchProviderVehiclesAsync(
              provider,
              _transitRefresh.HasUsableSnapshot(provider.Endpoint, refreshStartedAt),
              cancellationToken)))
        : Task.FromResult(Array.Empty<ProviderVehicleRefresh>());
      var routeMapRefreshTask = nearbyRealtimeProviders.Count > 0
        ? RefreshMissingRouteMapsAsync(nearbyRealtimeProviders, refreshStartedAt, cancellationToken)
        : Task.CompletedTask;
      var scheduledNetworksTask = nearbyScheduleProviders.Count > 0
        ? RefreshMissingScheduledNetworksAsync(nearbyScheduleProviders, refreshStartedAt, cancellationToken)
        : Task.CompletedTask;

      await Task.WhenAll(vehicleRefreshTask, routeMapRefreshTask, scheduledNetworksTask);
      var refreshResults = await vehicleRefreshTask;

      var nowAfterFetch = _utcNowProvider();
      TransitRefreshOutcome outcome;
      if (nearbyRealtimeProviders.Count > 0)
      {
        outcome = _transitRefresh.ApplyResults(nearbyRealtimeProviders, refreshResults, nowAfterFetch);
      }
      else
      {
        _transitRefresh.Clear();
        outcome = new TransitRefreshOutcome([], 0, 0, 0, 0, 0, VehiclesChanged: false);
      }

      var updatedRouteNames = ApplyRouteShortNames(outcome.Vehicles, nearbyRealtimeProviders);
      var estimatedVehicles = EstimateScheduledVehicles(nearbyScheduleProviders, nowAfterFetch);

      var combinedVehicles = estimatedVehicles.Count == 0
        ? outcome.Vehicles
        : [.. outcome.Vehicles, .. estimatedVehicles];

      var hadEstimatedVehicles = _hasEstimatedVehicles;
      _hasEstimatedVehicles = estimatedVehicles.Count > 0;

      _cachedVehicles = combinedVehicles;

      var successfulRefreshCount = outcome.AttemptedProviderCount - outcome.FailedProviderCount;
      if (successfulRefreshCount > 0 || estimatedVehicles.Count > 0)
      {
        _lastUpdated = MalaysiaTime.Convert(nowAfterFetch);
      }

      var statusMessage = BuildTransitStatus(outcome, totalNearbyProviderCount, estimatedVehicles.Count > 0);

      return new TransitTrackingResult
      {
        Vehicles = _cachedVehicles,
        StatusMessage = statusMessage,
        LastUpdated = _lastUpdated,
        AttemptedProviderCount = outcome.AttemptedProviderCount,
        FailedProviderCount = outcome.FailedProviderCount,
        StaleProviderCount = outcome.StaleProviderCount,
        ExpiredProviderCount = outcome.ExpiredProviderCount,
        NearbyProviderCount = totalNearbyProviderCount,
        HasNearbyFeeds = true,
        VehiclesChanged = outcome.VehiclesChanged || updatedRouteNames || _hasEstimatedVehicles || hadEstimatedVehicles
      };
    }
    finally
    {
      _refreshLock.Release();
    }
  }

  public Task<List<EnrichedRoute>> EnrichRoutesAsync(
    List<GoogleRoute> googleRoutes,
    CancellationToken cancellationToken = default) =>
    Task.FromResult(LiveRouting.EnrichRoutes(googleRoutes, [.. CachedVehicles]));

  public void Reset()
  {
    _transitRefresh.Clear();
    _cachedVehicles = [];
    _hasEstimatedVehicles = false;
    _lastUpdated = null;
    _staticCacheServiceDay = null;
    ClearDerivedStaticData();
  }

  private void ExpireDerivedStaticDataIfReset(DateTimeOffset utcNow)
  {
    var currentServiceDay = _mapCacheExpiration.GetStaticDataServiceDay(utcNow);
    if (_staticCacheServiceDay.HasValue && _staticCacheServiceDay.Value != currentServiceDay)
    {
      ClearDerivedStaticData();
    }
    _staticCacheServiceDay = currentServiceDay;
  }

  private void ClearDerivedStaticData()
  {
    _providerRouteMaps.Clear();
    _staticRouteRetryAfter.Clear();
    _providerFrequencies.Clear();
    _providerFrequenciesRetryAfter.Clear();
    _scheduledDayNetworks.Clear();
    _scheduledNetworkRetryAfter.Clear();
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    _refreshLock.Dispose();
  }

  private async Task<ProviderVehicleRefresh> FetchProviderVehiclesAsync(
    TransportProvider provider,
    bool allowNotModified,
    CancellationToken cancellationToken)
  {
    var result = await _gtfsService.GetBusPositionsAsync(
      provider.Endpoint,
      allowNotModified,
      cancellationToken);

    if (result.Status == GtfsFetchStatus.Failed)
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
      vehicle.ProviderName = provider.Name;
      vehicle.TransportType = provider.TransportType;
    }

    return new ProviderVehicleRefresh(
      provider.Endpoint,
      ProviderVehicleRefreshStatus.Updated,
      vehicles);
  }

  private async Task RefreshMissingRouteMapsAsync(
    IReadOnlyList<TransportProvider> nearbyProviders,
    DateTimeOffset now,
    CancellationToken cancellationToken)
  {
    if (_staticDataService == null) return;

    var providersToFetch = new List<TransportProvider>();
    var queuedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var provider in nearbyProviders)
    {
      if (!provider.HasStaticData) continue;

      var key = GetRouteMapKey(provider);
      if (!queuedKeys.Add(key) || _providerRouteMaps.ContainsKey(key)) continue;
      if (_staticRouteRetryAfter.TryGetValue(key, out var retryAt) && now < retryAt) continue;

      providersToFetch.Add(provider);
    }

    if (providersToFetch.Count == 0) return;

    var results = await Task.WhenAll(
      providersToFetch.Select(provider => FetchProviderRouteMapAsync(provider, cancellationToken)));

    foreach (var (provider, map) in results)
    {
      var key = GetRouteMapKey(provider);
      if (map != null)
      {
        _staticRouteRetryAfter.Remove(key);
        _providerRouteMaps[key] = map;
      }
      else
      {
        _staticRouteRetryAfter[key] = now.AddMinutes(1);
      }
    }
  }

  private async Task<(TransportProvider Provider, IReadOnlyDictionary<string, string>? Map)> FetchProviderRouteMapAsync(
    TransportProvider provider,
    CancellationToken cancellationToken)
  {
    try
    {
      var routes = await _staticDataService!.GetRoutesAsync(provider.StaticData!, cancellationToken);
      var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var route in routes)
      {
        var id = route.RouteId?.Trim();
        var name = (string.IsNullOrWhiteSpace(route.RouteShortName) ? route.RouteId : route.RouteShortName)?.Trim();
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
        {
          map[id] = name;
        }
      }

      return (provider, map);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogDebug("Static route lookup failed for {Provider}: {Message}", provider.Name, ex.Message);
      return (provider, null);
    }
  }

  private bool ApplyRouteShortNames(
    List<BusLocation> vehicles,
    IReadOnlyList<TransportProvider> nearbyProviders)
  {
    if (vehicles.Count == 0) return false;

    var routeMapsByProvider = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
    foreach (var provider in nearbyProviders)
    {
      if (provider.HasStaticData && _providerRouteMaps.TryGetValue(GetRouteMapKey(provider), out var map))
      {
        routeMapsByProvider[provider.Name] = map;
      }
    }

    if (routeMapsByProvider.Count == 0) return false;

    var updated = false;
    foreach (var vehicle in vehicles)
    {
      if (!string.IsNullOrWhiteSpace(vehicle.RouteShortName) ||
          string.IsNullOrWhiteSpace(vehicle.RouteId) ||
          string.IsNullOrWhiteSpace(vehicle.ProviderName))
      {
        continue;
      }

      if (routeMapsByProvider.TryGetValue(vehicle.ProviderName, out var routeMap) &&
          routeMap.TryGetValue(vehicle.RouteId.Trim(), out var shortName))
      {
        vehicle.RouteShortName = shortName;
        updated = true;
      }
    }

    return updated;
  }

  private async Task RefreshMissingScheduledNetworksAsync(
    IReadOnlyList<TransportProvider> nearbyScheduleProviders,
    DateTimeOffset now,
    CancellationToken cancellationToken)
  {
    if (_staticDataService == null) return;

    var todayDate = GetServiceDate(now);
    PruneStaleScheduledDayNetworks(todayDate);

    var freqProviders = new List<TransportProvider>();
    var queuedFreqKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var provider in nearbyScheduleProviders)
    {
      if (!provider.HasFrequencyFeed) continue;
      var freqKey = GetFrequencyKey(provider);
      if (!queuedFreqKeys.Add(freqKey) || _providerFrequencies.ContainsKey(freqKey)) continue;
      if (_providerFrequenciesRetryAfter.TryGetValue(freqKey, out var retryAt) && now < retryAt) continue;
      freqProviders.Add(provider);
    }

    if (freqProviders.Count > 0)
    {
      var freqResults = await Task.WhenAll(
        freqProviders.Select(provider => FetchProviderFrequenciesAsync(provider, cancellationToken)));
      foreach (var (provider, frequencies) in freqResults)
      {
        var freqKey = GetFrequencyKey(provider);
        if (frequencies != null)
        {
          _providerFrequenciesRetryAfter.Remove(freqKey);
          _providerFrequencies[freqKey] = frequencies;
        }
        else
        {
          _providerFrequenciesRetryAfter[freqKey] = now.AddMinutes(1);
        }
      }
    }

    var mapRequests = new List<(TransportProvider Provider, IReadOnlyList<StaticFrequency> Frequencies)>();
    var queuedDayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var provider in nearbyScheduleProviders)
    {
      if (!provider.HasFrequencyFeed) continue;
      if (!_providerFrequencies.TryGetValue(GetFrequencyKey(provider), out var frequencies) || frequencies.Count == 0) continue;

      var dayKey = GetScheduledDayKey(provider, todayDate);
      if (!queuedDayKeys.Add(dayKey) || _scheduledDayNetworks.ContainsKey(dayKey)) continue;
      if (_scheduledNetworkRetryAfter.TryGetValue(dayKey, out var retryAt) && now < retryAt) continue;
      mapRequests.Add((provider, frequencies));
    }

    if (mapRequests.Count == 0) return;

    var dayResults = await Task.WhenAll(
      mapRequests.Select(req => FetchScheduledDayNetworkAsync(req.Provider, todayDate, req.Frequencies, cancellationToken)));

    foreach (var (provider, network) in dayResults)
    {
      var dayKey = GetScheduledDayKey(provider, todayDate);
      if (network != null)
      {
        _scheduledNetworkRetryAfter.Remove(dayKey);
        _scheduledDayNetworks[dayKey] = network;
      }
      else
      {
        _scheduledNetworkRetryAfter[dayKey] = now.AddMinutes(1);
      }
    }
  }

  private async Task<(TransportProvider Provider, IReadOnlyList<StaticFrequency>? Frequencies)> FetchProviderFrequenciesAsync(
    TransportProvider provider,
    CancellationToken cancellationToken)
  {
    try
    {
      var frequencies = await _staticDataService!.GetFrequenciesAsync(provider.StaticData!, cancellationToken);
      return (provider, frequencies);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogDebug("Static frequencies lookup failed for {Provider}: {Message}", provider.Name, ex.Message);
      return (provider, null);
    }
  }

  private async Task<(TransportProvider Provider, ScheduledRailDayNetwork? Network)> FetchScheduledDayNetworkAsync(
    TransportProvider provider,
    string serviceDate,
    IReadOnlyList<StaticFrequency> frequencies,
    CancellationToken cancellationToken)
  {
    try
    {
      var mapGeoJson = await _staticDataService!.GetMapAsync(provider.StaticData!, "", serviceDate, cancellationToken);
      if (string.IsNullOrWhiteSpace(mapGeoJson))
      {
        return (provider, null);
      }

      return (provider, ScheduledRailPositionEstimator.CompileForServiceDay(mapGeoJson, frequencies));
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogDebug("Scheduled rail map lookup failed for {Provider} ({Date}): {Message}", provider.Name, serviceDate, ex.Message);
      return (provider, null);
    }
  }

  private List<BusLocation> EstimateScheduledVehicles(
    IReadOnlyList<TransportProvider> nearbyScheduleProviders,
    DateTimeOffset utcNow)
  {
    if (nearbyScheduleProviders.Count == 0) return [];

    var todayDate = GetServiceDate(utcNow);
    var estimated = new List<BusLocation>();
    foreach (var provider in nearbyScheduleProviders)
    {
      if (!provider.HasFrequencyFeed) continue;

      if (!_scheduledDayNetworks.TryGetValue(GetScheduledDayKey(provider, todayDate), out var todayNetwork))
      {
        continue;
      }

      estimated.AddRange(ScheduledRailPositionEstimator.EstimateVehicles(todayNetwork, provider, utcNow));
    }
    return estimated;
  }

  private static string GetServiceDate(DateTimeOffset utcNow) =>
    FormatServiceDate(MalaysiaTime.Convert(utcNow).Date);

  private void PruneStaleScheduledDayNetworks(string todayDate)
  {
    if (_scheduledDayNetworks.Count == 0) return;

    var suffix = $"|{todayDate}";
    var staleKeys = new List<string>();
    foreach (var key in _scheduledDayNetworks.Keys)
    {
      if (!key.EndsWith(suffix, StringComparison.Ordinal))
      {
        staleKeys.Add(key);
      }
    }

    foreach (var key in staleKeys)
    {
      _scheduledDayNetworks.Remove(key);
      _scheduledNetworkRetryAfter.Remove(key);
    }
  }

  private static string FormatServiceDate(DateTime date) =>
    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  private static string GetFrequencyKey(TransportProvider provider) =>
    $"{provider.StaticData!.BaseUrl.Trim()}|{provider.StaticData.FrequenciesPath.Trim()}";

  private static string GetScheduledDayKey(TransportProvider provider, string serviceDate) =>
    $"{provider.StaticData!.BaseUrl.Trim()}|{serviceDate}";

  private static string GetRouteMapKey(TransportProvider provider) =>
    provider.StaticData!.BaseUrl.Trim();

  private string BuildTransitStatus(TransitRefreshOutcome outcome, int nearbyProviderCount, bool hasEstimatedVehicles)
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
    var estimatedSuffix = hasEstimatedVehicles
      ? ", incl. estimated rail"
      : string.Empty;

    if (!_lastUpdated.HasValue)
    {
      return $"Showing {_cachedVehicles.Count} buses ({nearbyProviderCount} source(s){staleSuffix}{expiredSuffix}{estimatedSuffix})";
    }

    return $"Buses updated: {_lastUpdated:HH:mm:ss} ({nearbyProviderCount} source(s){staleSuffix}{expiredSuffix}{estimatedSuffix})";
  }
}

