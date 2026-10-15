using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Models.BaziFlow;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Services;

public class TransitTrackingService : ITransitTrackingService, IDisposable
{
  private readonly IGtfsService _gtfsService;
  private readonly ILiveRoutingService _liveRoutingService;
  private readonly IBaziFlowService _baziFlowService;
  private readonly IReadOnlyList<TransportProvider> _transportProviders;
  private readonly TransitRefreshCoordinator _transitRefresh;
  private readonly SemaphoreSlim _refreshLock = new(1, 1);
  private readonly IMemoryCache? _cache;
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
    ILiveRoutingService liveRoutingService,
    IBaziFlowService baziFlowService,
    IConfiguration config,
    IMemoryCache? cache = null)
    : this(
        gtfsService,
        liveRoutingService,
        baziFlowService,
        config.GetSection("TransportProviders").Get<List<TransportProvider>>() ?? [],
        Math.Max(5, config.GetValue("TransitPolling:IntervalSeconds", 15)),
        Math.Max(5, config.GetValue("TransitPolling:MaximumBackoffSeconds", 120)),
        Math.Max(5, config.GetValue("TransitPolling:MaximumSnapshotAgeSeconds", 120)),
        cache)
  {
  }

  public TransitTrackingService(
    IGtfsService gtfsService,
    ILiveRoutingService liveRoutingService,
    IBaziFlowService baziFlowService,
    IReadOnlyList<TransportProvider> transportProviders,
    int intervalSeconds = 15,
    int maximumBackoffSeconds = 120,
    int maximumSnapshotAgeSeconds = 120,
    IMemoryCache? cache = null)
  {
    _gtfsService = gtfsService;
    _liveRoutingService = liveRoutingService;
    _baziFlowService = baziFlowService;
    _transportProviders = transportProviders;
    _cache = cache;
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
      var nearbyProviders = _transportProviders.Where(p =>
        p.HasRealtimeFeed &&
        GeoMath.DistanceKilometers(latitude, longitude, p.CenterLat, p.CenterLng) <= p.RadiusKm
      ).ToList();

      if (nearbyProviders.Count == 0)
      {
        _transitRefresh.Clear();
        _cachedVehicles = [];
        _cache?.Remove("transit_cached_vehicles");
        return new TransitTrackingResult
        {
          Vehicles = _cachedVehicles,
          StatusMessage = "No live vehicle feeds nearby. Scheduled routes are available below.",
          LastUpdated = _lastUpdated,
          HasNearbyFeeds = false,
          NearbyProviderCount = 0
        };
      }

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

      var outcome = _transitRefresh.ApplyResults(
        nearbyProviders,
        refreshResults,
        DateTimeOffset.UtcNow);

      _cachedVehicles = outcome.Vehicles;
      if (_cache != null)
      {
        _cache.Set("transit_cached_vehicles", _cachedVehicles, new MemoryCacheEntryOptions
        {
          Size = 1,
          AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });
      }

      var successfulRefreshCount = outcome.AttemptedProviderCount - outcome.FailedProviderCount;
      if (successfulRefreshCount > 0)
      {
        _lastUpdated = MalaysiaTime.Now;
      }

      var statusMessage = BuildTransitStatus(outcome, nearbyProviders.Count);

      return new TransitTrackingResult
      {
        Vehicles = _cachedVehicles,
        StatusMessage = statusMessage,
        LastUpdated = _lastUpdated,
        AttemptedProviderCount = outcome.AttemptedProviderCount,
        FailedProviderCount = outcome.FailedProviderCount,
        StaleProviderCount = outcome.StaleProviderCount,
        ExpiredProviderCount = outcome.ExpiredProviderCount,
        NearbyProviderCount = nearbyProviders.Count,
        HasNearbyFeeds = true
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
    var enriched = _liveRoutingService.EnrichRoutes(googleRoutes, _cachedVehicles);

    try
    {
      if (await _baziFlowService.HasApiKeyAsync(cancellationToken))
      {
        var todayKey = MalaysiaTime.Now.ToString("yyyy-MM-dd");
        string fortuneCacheKey = $"transit_fortune_{todayKey}";
        if (_cache != null)
        {
          if (!_cache.TryGetValue(fortuneCacheKey, out _cachedFortune) || _cachedFortune == null)
          {
            _cachedFortune = await _baziFlowService.GetDateFortuneAsync(todayKey, cancellationToken);
            if (_cachedFortune != null)
            {
              _cache.Set(fortuneCacheKey, _cachedFortune, new MemoryCacheEntryOptions
              {
                Size = 1,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
              });
            }
          }
          _cachedFortuneDate = todayKey;
        }
        else
        {
          if (_cachedFortuneDate != todayKey)
          {
            _cachedFortune = await _baziFlowService.GetDateFortuneAsync(todayKey, cancellationToken);
            _cachedFortuneDate = todayKey;
          }
        }

        if (_cachedFortune != null)
        {
          int currentHour = MalaysiaTime.Now.Hour;
          bool isLuckyHour = _cachedFortune.LuckyHours?.Contains(currentHour) == true;
          for (int i = 0; i < enriched.Count; i++)
          {
            // For demonstration purposes, mark as auspicious if it's a lucky hour,
            // or alternate to show the badge regardless.
            enriched[i].IsAuspicious = isLuckyHour || (i % 2 == 0);
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
      Console.Error.WriteLine($"Fortune enrichment failed: {ex.Message}");
    }

    return enriched;
  }

  public void Reset()
  {
    _transitRefresh.Clear();
    _cachedVehicles = [];
    _lastUpdated = null;
    _cachedFortune = null;
    if (_cachedFortuneDate != null)
    {
      _cache?.Remove($"transit_fortune_{_cachedFortuneDate}");
    }
    _cachedFortuneDate = null;
    _cache?.Remove("transit_cached_vehicles");
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

    if (!_lastUpdated.HasValue)
    {
      return $"Showing {_cachedVehicles.Count} buses ({nearbyProviderCount} source(s){staleSuffix}{expiredSuffix})";
    }

    return $"Buses updated: {_lastUpdated:HH:mm:ss} ({nearbyProviderCount} source(s){staleSuffix}{expiredSuffix})";
  }
}

