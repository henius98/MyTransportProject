using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Features.Transit.Services;

public sealed class TransitTrackingService : ITransitTrackingService, IDisposable
{
  private readonly IGtfsService _gtfsService;
  private readonly IBaziFlowService _baziFlowService;
  private readonly ITransportStaticDataService? _staticDataService;
  private readonly IReadOnlyList<TransportProvider> _transportProviders;
  private readonly TransitRefreshCoordinator _transitRefresh;
  private readonly SemaphoreSlim _refreshLock = new(1, 1);
  private readonly IAppCache? _cache;
  private readonly ILogger<TransitTrackingService> _logger;
  private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _providerRouteMaps = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, DateTimeOffset> _staticRouteRetryAfter = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _routeMapCacheKeys = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IReadOnlyList<StaticFrequency>> _providerFrequencies = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, DateTimeOffset> _providerFrequenciesRetryAfter = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _frequencyCacheKeys = new(StringComparer.Ordinal);
  private readonly Dictionary<string, ScheduledRailDayNetwork> _scheduledDayNetworks = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, DateTimeOffset> _scheduledNetworkRetryAfter = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _scheduledNetworkCacheKeys = new(StringComparer.Ordinal);
  private readonly Func<DateTimeOffset> _utcNowProvider;
  private bool _hasEstimatedVehicles;
  private bool _disposed;
  private FortuneData? _cachedFortune;
  private string? _cachedFortuneDate;
  private List<BusLocation> _cachedVehicles = [];
  private DateTimeOffset? _lastUpdated;

  public IReadOnlyList<BusLocation> CachedVehicles =>
    (_cache != null && _cache.TryGetValue("transit_cached_vehicles", out List<BusLocation>? cached) && cached != null)
      ? cached
      : _cachedVehicles;

  [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
  public TransitTrackingService(
    IGtfsService gtfsService,
    IBaziFlowService baziFlowService,
    IOptions<TransitOptions> transitOptions,
    IAppCache? cache = null,
    ILogger<TransitTrackingService>? logger = null,
    ITransportStaticDataService? staticDataService = null)
    : this(
        gtfsService,
        baziFlowService,
        transitOptions.Value.Providers,
        transitOptions.Value.Polling.IntervalSeconds,
        transitOptions.Value.Polling.MaximumBackoffSeconds,
        transitOptions.Value.Polling.MaximumSnapshotAgeSeconds,
        cache,
        logger,
        staticDataService)
  {
  }

  public TransitTrackingService(
    IGtfsService gtfsService,
    IBaziFlowService baziFlowService,
    IReadOnlyList<TransportProvider> transportProviders,
    int intervalSeconds = 15,
    int maximumBackoffSeconds = 120,
    int maximumSnapshotAgeSeconds = 120,
    IAppCache? cache = null,
    ILogger<TransitTrackingService>? logger = null,
    ITransportStaticDataService? staticDataService = null,
    Func<DateTimeOffset>? utcNowProvider = null)
  {
    _gtfsService = gtfsService;
    _baziFlowService = baziFlowService;
    _staticDataService = staticDataService;
    _transportProviders = transportProviders;
    _cache = cache;
    _logger = logger ?? NullLogger<TransitTrackingService>.Instance;
    _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
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
        _cache?.Remove("transit_cached_vehicles");
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
      _cache?.Set("transit_cached_vehicles", _cachedVehicles, TimeSpan.FromMinutes(5));

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

  public async Task<List<EnrichedRoute>> EnrichRoutesAsync(
    List<GoogleRoute> googleRoutes,
    CancellationToken cancellationToken = default)
  {
    var enriched = LiveRouting.EnrichRoutes(googleRoutes, [.. CachedVehicles]);

    try
    {
      if (await _baziFlowService.HasApiKeyAsync(cancellationToken))
      {
        var todayKey = MalaysiaTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string fortuneCacheKey = $"transit_fortune_{todayKey}";
        if (_cache != null)
        {
          if (!_cache.TryGetValue(fortuneCacheKey, out _cachedFortune) || _cachedFortune == null)
          {
            _cachedFortune = await _baziFlowService.GetDateFortuneAsync(todayKey, cancellationToken);
            if (_cachedFortune != null)
            {
              _cache.Set(fortuneCacheKey, _cachedFortune, TimeSpan.FromHours(24));
            }
          }
          if (_cachedFortune != null)
          {
            _cachedFortuneDate = todayKey;
          }
        }
        else
        {
          if (_cachedFortuneDate != todayKey)
          {
            _cachedFortune = await _baziFlowService.GetDateFortuneAsync(todayKey, cancellationToken);
            if (_cachedFortune != null)
            {
              _cachedFortuneDate = todayKey;
            }
          }
        }

        if (_cachedFortune != null)
        {
          int currentHour = MalaysiaTime.Now.Hour;
          bool isLuckyHour = _cachedFortune.LuckyHours?.Contains(currentHour) == true;
          for (int i = 0; i < enriched.Count; i++)
          {
            // Mark as auspicious when the current hour falls within the day's lucky hours.
            enriched[i].IsAuspicious = isLuckyHour;
          }
        }
      }
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogDebug("Fortune enrichment failed: {Message}", ex.Message);
    }

    return enriched;
  }

  public void Reset()
  {
    _transitRefresh.Clear();
    _cachedVehicles = [];
    _hasEstimatedVehicles = false;
    _lastUpdated = null;
    _cachedFortune = null;
    if (_cachedFortuneDate != null)
    {
      _cache?.Remove($"transit_fortune_{_cachedFortuneDate}");
    }
    _cachedFortuneDate = null;
    _cache?.Remove("transit_cached_vehicles");
    _providerRouteMaps.Clear();
    _staticRouteRetryAfter.Clear();
    _providerFrequencies.Clear();
    _providerFrequenciesRetryAfter.Clear();
    _scheduledDayNetworks.Clear();
    _scheduledNetworkRetryAfter.Clear();
    if (_cache != null)
    {
      foreach (var cacheKey in _routeMapCacheKeys)
      {
        _cache.Remove(cacheKey);
      }
      foreach (var cacheKey in _frequencyCacheKeys)
      {
        _cache.Remove(cacheKey);
      }
      foreach (var cacheKey in _scheduledNetworkCacheKeys)
      {
        _cache.Remove(cacheKey);
      }
    }
    _routeMapCacheKeys.Clear();
    _frequencyCacheKeys.Clear();
    _scheduledNetworkCacheKeys.Clear();
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
      if (!queuedKeys.Add(key) || TryGetCachedRouteMap(key, out _)) continue;
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
        _providerRouteMaps[key] = map;
        _staticRouteRetryAfter.Remove(key);
        if (_cache != null)
        {
          var cacheKey = GetRouteMapCacheKey(key);
          _routeMapCacheKeys.Add(cacheKey);
          _cache.Set(cacheKey, map, TimeSpan.FromHours(24));
        }
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
      if (provider.HasStaticData && TryGetCachedRouteMap(GetRouteMapKey(provider), out var map))
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

  private bool TryGetCachedRouteMap(string key, out IReadOnlyDictionary<string, string> map)
  {
    if (_providerRouteMaps.TryGetValue(key, out map!))
    {
      return true;
    }

    var cacheKey = GetRouteMapCacheKey(key);
    if (_cache != null &&
        _cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, string>? cached) &&
        cached != null)
    {
      _providerRouteMaps[key] = cached;
      map = cached;
      return true;
    }

    map = null!;
    return false;
  }

  private async Task RefreshMissingScheduledNetworksAsync(
    IReadOnlyList<TransportProvider> nearbyScheduleProviders,
    DateTimeOffset now,
    CancellationToken cancellationToken)
  {
    if (_staticDataService == null) return;

    var requiredDates = GetRequiredServiceDates(now);
    PruneStaleScheduledDayNetworks(requiredDates);

    var freqProviders = new List<TransportProvider>();
    var queuedFreqKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var provider in nearbyScheduleProviders)
    {
      if (!provider.HasFrequencyFeed) continue;
      var freqKey = GetFrequencyKey(provider);
      if (!queuedFreqKeys.Add(freqKey) || TryGetCachedFrequencies(freqKey, out _)) continue;
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
          _providerFrequencies[freqKey] = frequencies;
          _providerFrequenciesRetryAfter.Remove(freqKey);
          if (_cache != null)
          {
            var cacheKey = GetFrequencyCacheKey(freqKey);
            _frequencyCacheKeys.Add(cacheKey);
            _cache.Set(cacheKey, frequencies, TimeSpan.FromHours(1));
          }
        }
        else
        {
          _providerFrequenciesRetryAfter[freqKey] = now.AddMinutes(1);
        }
      }
    }

    var mapRequests = new List<(TransportProvider Provider, string ServiceDate, IReadOnlyList<StaticFrequency> Frequencies)>();
    var queuedDayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var provider in nearbyScheduleProviders)
    {
      if (!provider.HasFrequencyFeed) continue;
      if (!TryGetCachedFrequencies(GetFrequencyKey(provider), out var frequencies) || frequencies.Count == 0) continue;

      foreach (var serviceDate in requiredDates)
      {
        var dayKey = GetScheduledDayKey(provider, serviceDate);
        if (!queuedDayKeys.Add(dayKey) || TryGetCachedScheduledDayNetwork(dayKey, out _)) continue;
        if (_scheduledNetworkRetryAfter.TryGetValue(dayKey, out var retryAt) && now < retryAt) continue;
        mapRequests.Add((provider, serviceDate, frequencies));
      }
    }

    if (mapRequests.Count == 0) return;

    var dayResults = await Task.WhenAll(
      mapRequests.Select(req => FetchScheduledDayNetworkAsync(req.Provider, req.ServiceDate, req.Frequencies, cancellationToken)));

    foreach (var (provider, serviceDate, network) in dayResults)
    {
      var dayKey = GetScheduledDayKey(provider, serviceDate);
      if (network != null)
      {
        _scheduledDayNetworks[dayKey] = network;
        _scheduledNetworkRetryAfter.Remove(dayKey);
        if (_cache != null)
        {
          var cacheKey = GetScheduledDayCacheKey(dayKey);
          _scheduledNetworkCacheKeys.Add(cacheKey);
          _cache.Set(cacheKey, network, TimeSpan.FromHours(1));
        }
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

  private async Task<(TransportProvider Provider, string ServiceDate, ScheduledRailDayNetwork? Network)> FetchScheduledDayNetworkAsync(
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
        return (provider, serviceDate, null);
      }

      return (provider, serviceDate, ScheduledRailPositionEstimator.CompileForServiceDay(mapGeoJson, frequencies));
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogDebug("Scheduled rail map lookup failed for {Provider} ({Date}): {Message}", provider.Name, serviceDate, ex.Message);
      return (provider, serviceDate, null);
    }
  }

  private List<BusLocation> EstimateScheduledVehicles(
    IReadOnlyList<TransportProvider> nearbyScheduleProviders,
    DateTimeOffset utcNow)
  {
    if (nearbyScheduleProviders.Count == 0) return [];

    var localNow = MalaysiaTime.Convert(utcNow);
    var todayDate = FormatServiceDate(localNow.Date);
    var yesterdayDate = localNow.TimeOfDay.TotalSeconds < ScheduledRailPositionEstimator.PostMidnightLookbackSeconds
      ? FormatServiceDate(localNow.Date.AddDays(-1))
      : null;

    var estimated = new List<BusLocation>();
    foreach (var provider in nearbyScheduleProviders)
    {
      if (!provider.HasFrequencyFeed) continue;

      TryGetCachedScheduledDayNetwork(GetScheduledDayKey(provider, todayDate), out var todayNetwork);
      ScheduledRailDayNetwork? yesterdayNetwork = null;
      if (yesterdayDate != null)
      {
        TryGetCachedScheduledDayNetwork(GetScheduledDayKey(provider, yesterdayDate), out yesterdayNetwork);
      }

      if (todayNetwork == null && yesterdayNetwork == null) continue;
      estimated.AddRange(ScheduledRailPositionEstimator.EstimateVehicles(todayNetwork, yesterdayNetwork, provider, utcNow));
    }
    return estimated;
  }

  private static string[] GetRequiredServiceDates(DateTimeOffset utcNow)
  {
    var localNow = MalaysiaTime.Convert(utcNow);
    var today = FormatServiceDate(localNow.Date);
    return localNow.TimeOfDay.TotalSeconds < ScheduledRailPositionEstimator.PostMidnightLookbackSeconds
      ? [today, FormatServiceDate(localNow.Date.AddDays(-1))]
      : [today];
  }

  private void PruneStaleScheduledDayNetworks(string[] requiredDates)
  {
    if (_scheduledDayNetworks.Count == 0) return;

    var staleKeys = new List<string>();
    foreach (var key in _scheduledDayNetworks.Keys)
    {
      var keep = false;
      foreach (var date in requiredDates)
      {
        if (key.EndsWith($"|{date}", StringComparison.Ordinal))
        {
          keep = true;
          break;
        }
      }
      if (!keep) staleKeys.Add(key);
    }

    foreach (var key in staleKeys)
    {
      _scheduledDayNetworks.Remove(key);
      _scheduledNetworkRetryAfter.Remove(key);
    }
  }

  private bool TryGetCachedFrequencies(string key, out IReadOnlyList<StaticFrequency> frequencies)
  {
    if (_providerFrequencies.TryGetValue(key, out frequencies!))
    {
      return true;
    }

    var cacheKey = GetFrequencyCacheKey(key);
    if (_cache != null &&
        _cache.TryGetValue(cacheKey, out IReadOnlyList<StaticFrequency>? cached) &&
        cached != null)
    {
      _providerFrequencies[key] = cached;
      frequencies = cached;
      return true;
    }

    frequencies = null!;
    return false;
  }

  private bool TryGetCachedScheduledDayNetwork(string key, out ScheduledRailDayNetwork network)
  {
    if (_scheduledDayNetworks.TryGetValue(key, out network!))
    {
      return true;
    }

    var cacheKey = GetScheduledDayCacheKey(key);
    if (_cache != null &&
        _cache.TryGetValue(cacheKey, out ScheduledRailDayNetwork? cached) &&
        cached != null)
    {
      _scheduledDayNetworks[key] = cached;
      network = cached;
      return true;
    }

    network = null!;
    return false;
  }

  private static string FormatServiceDate(DateTime date) =>
    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  private static string GetFrequencyKey(TransportProvider provider) =>
    $"{provider.StaticData!.BaseUrl.Trim()}|{provider.StaticData.FrequenciesPath.Trim()}";

  private static string GetFrequencyCacheKey(string key) =>
    $"transit_static_frequencies_{key}";

  private static string GetScheduledDayKey(TransportProvider provider, string serviceDate) =>
    $"{provider.StaticData!.BaseUrl.Trim()}|{serviceDate}";

  private static string GetScheduledDayCacheKey(string key) =>
    $"transit_scheduled_day_{key}";

  private static string GetRouteMapKey(TransportProvider provider) =>
    provider.StaticData!.BaseUrl.Trim();

  private static string GetRouteMapCacheKey(string key) =>
    $"transit_static_routes_{key}";

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

