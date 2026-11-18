using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Features.Weather.Services
{
  public sealed class WeatherPlannerService : IWeatherPlannerService
  {
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly IPlaceResolutionService? _placeResolutionService;
    private readonly IWeatherStationCatalogService? _stationCatalog;
    private readonly TimeSpan _liveCacheExpiration;
    private readonly TimeSpan _outlookCacheExpiration;
    private readonly TimeSpan _fallbackCacheExpiration;
    private readonly OpenMeteoWeatherAdapter _openMeteo;
    private readonly IReadOnlyList<IWeatherProviderAdapter> _adapters;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public WeatherPlannerService(
      HttpClient httpClient,
      IMemoryCache cache,
      IOptions<CacheExpirationOptions> cacheExpiration,
      IOptions<WeatherOptions> weatherOptions,
      IPlaceResolutionService? placeResolutionService = null,
      IWeatherStationCatalogService? stationCatalog = null)
    {
      _httpClient = httpClient;
      _cache = cache;
      var options = weatherOptions.Value ?? new();
      _placeResolutionService = placeResolutionService;
      _stationCatalog = stationCatalog;
      _liveCacheExpiration = GetPositiveDuration(
        cacheExpiration.Value.Weather.LiveSeconds,
        $"{nameof(CacheExpirationOptions.Weather)}:{nameof(WeatherCacheExpirationOptions.LiveSeconds)}");
      _outlookCacheExpiration = GetPositiveDuration(
        cacheExpiration.Value.Weather.OutlookSeconds,
        $"{nameof(CacheExpirationOptions.Weather)}:{nameof(WeatherCacheExpirationOptions.OutlookSeconds)}");
      _fallbackCacheExpiration = GetPositiveDuration(
        cacheExpiration.Value.Weather.FallbackSeconds,
        $"{nameof(CacheExpirationOptions.Weather)}:{nameof(WeatherCacheExpirationOptions.FallbackSeconds)}");

      _openMeteo = new OpenMeteoWeatherAdapter(options.OpenMeteo);
      _adapters =
      [
        new MetMalaysiaWeatherAdapter(options.MetMalaysia),
        _openMeteo,
        new SingaporeNeaWeatherAdapter(options.SingaporeNEA)
      ];
    }

    public async Task<WeatherPlanResult> GetPlanAsync(
      WeatherQuery query,
      CancellationToken cancellationToken = default,
      bool forceRefresh = false)
    {
      ArgumentNullException.ThrowIfNull(query);

      var resolvedQuery = query with { };
      var hasPendingSearch = !string.IsNullOrWhiteSpace(resolvedQuery.PendingSearchText);
      if (!hasPendingSearch && !GeoMath.IsValidCoordinates(resolvedQuery.Latitude, resolvedQuery.Longitude))
      {
        return InvalidPlan(resolvedQuery, "Enter a valid latitude (-90 to 90) and longitude (-180 to 180).");
      }

      var minDate = MalaysiaTime.Today;
      var maxDate = minDate.AddDays(WeatherForecastLimits.OpenMeteoForecastDays - 1);
      var departDate = resolvedQuery.Departure.HasValue ? MalaysiaTime.Convert(resolvedQuery.Departure.Value).Date : (DateTime?)null;
      var returnDate = resolvedQuery.Return.HasValue ? MalaysiaTime.Convert(resolvedQuery.Return.Value).Date : (DateTime?)null;

      if ((departDate.HasValue && (departDate.Value < minDate || departDate.Value > maxDate))
        || (returnDate.HasValue && (returnDate.Value < minDate || returnDate.Value > maxDate)))
      {
        return InvalidPlan(
          resolvedQuery,
          $"Select dates within the {WeatherForecastLimits.OpenMeteoForecastDays}-day forecast window ending {maxDate:dd MMM yyyy}.");
      }

      if (departDate.HasValue && returnDate.HasValue && returnDate.Value < departDate.Value)
      {
        return InvalidPlan(resolvedQuery, "Return date must be on or after the departure date.");
      }

      if (hasPendingSearch)
      {
        var searchText = resolvedQuery.PendingSearchText!.Trim();
        var suggestions = _placeResolutionService != null
          ? await _placeResolutionService.GetSuggestionsAsync(resolvedQuery.PendingSearchText!, cancellationToken)
          : [];
        var exactMatches = suggestions.Where(suggestion =>
          string.Equals(suggestion.Name, searchText, StringComparison.OrdinalIgnoreCase)
          || (!string.IsNullOrWhiteSpace(suggestion.LocationId)
              && string.Equals(suggestion.LocationId, searchText, StringComparison.OrdinalIgnoreCase))).ToList();

        PlaceSuggestion chosen;
        if (exactMatches.Count == 1)
        {
          chosen = exactMatches[0];
        }
        else if (suggestions.Count == 1)
        {
          chosen = suggestions[0];
        }
        else
        {
          var error = suggestions.Count == 0
            ? "Enter a known forecast name or ID, or valid latitude (-90 to 90), longitude (-180 to 180)."
            : "Several forecast locations match. Select a result from the location search.";
          return InvalidPlan(resolvedQuery, error);
        }

        resolvedQuery.ApplyPlace(chosen);
      }

      if (string.IsNullOrWhiteSpace(resolvedQuery.LocationId))
      {
        await ResolveNearestLocationAsync(resolvedQuery, updatePlaceName: false, cancellationToken);
      }

      var realtimeTasks = new List<Task<WeatherProviderResult>>();
      var outlookTasks = new List<Task<WeatherProviderResult>>();

      foreach (var adapter in _adapters)
      {
        if (!adapter.IsEligible(resolvedQuery)) continue;

        realtimeTasks.Add(FetchRealtimeSliceAsync(
          adapter,
          adapter.BuildEndpoint(resolvedQuery, isOutlook: false),
          forceRefresh,
          cancellationToken));

        if (resolvedQuery.Return.HasValue)
        {
          outlookTasks.Add(FetchOutlookSlicesAsync(
            adapter,
            adapter.BuildEndpoint(resolvedQuery, isOutlook: true),
            adapter.OutlookMaxDays,
            forceRefresh,
            cancellationToken));
        }
      }

      var realtimeAllTask = Task.WhenAll(realtimeTasks);
      var outlookAllTask = Task.WhenAll(outlookTasks);
      await Task.WhenAll(realtimeAllTask, outlookAllTask);

      return new WeatherPlanResult
      {
        Query = resolvedQuery,
        Realtime = (await realtimeAllTask).Where(r => r != null).ToList(),
        Outlooks = (await outlookAllTask).Where(r => r != null).ToList()
      };
    }

    public async Task ResolveNearestLocationAsync(
      WeatherQuery query,
      bool updatePlaceName = false,
      CancellationToken cancellationToken = default)
    {
      ArgumentNullException.ThrowIfNull(query);
      if (_stationCatalog == null) return;

      var nearest = await _stationCatalog.FindNearestStationAsync(
        query.Latitude,
        query.Longitude,
        cancellationToken);

      query.LocationId = nearest?.LocationId;

      if (updatePlaceName && !string.IsNullOrWhiteSpace(nearest?.LocationName))
      {
        query.PlaceName = nearest.LocationName;
      }
    }

    private static WeatherPlanResult InvalidPlan(WeatherQuery query, string validationError) => new()
    {
      Query = query with { },
      Realtime = Array.Empty<WeatherProviderResult>(),
      Outlooks = Array.Empty<WeatherProviderResult>(),
      ValidationError = validationError
    };

    public async Task<WeatherProviderResult> RefreshProviderAsync(
      string providerName,
      WeatherQuery query,
      bool isOutlook,
      CancellationToken cancellationToken = default)
    {
      ArgumentNullException.ThrowIfNull(query);

      var adapter = _adapters.FirstOrDefault(a =>
        string.Equals(a.Name, providerName, StringComparison.OrdinalIgnoreCase));
      if (adapter is null)
      {
        throw new ArgumentException($"Unknown weather provider '{providerName}'", nameof(providerName));
      }

      var endpoint = adapter.BuildEndpoint(query, isOutlook);
      return isOutlook
        ? await FetchOutlookSlicesAsync(adapter, endpoint, adapter.OutlookMaxDays, forceRefresh: true, cancellationToken)
        : await FetchRealtimeSliceAsync(adapter, endpoint, forceRefresh: true, cancellationToken);
    }

    public async Task<WeatherProviderResult> GetHourlyForecastAsync(
      WeatherQuery query,
      DateTime date,
      CancellationToken cancellationToken = default,
      bool forceRefresh = false)
    {
      ArgumentNullException.ThrowIfNull(query);

      if (!_openMeteo.TryBuildHourlyEndpoint(query, date, out var uri) || uri is null)
      {
        return BuildSample(
          _openMeteo,
          "Unavailable",
          WeatherBadgeTone.Error,
          Array.Empty<WeatherTimeSlice>());
      }

      var result = await FetchProviderDataAsync(_openMeteo, uri, "Outlook", cancellationToken, forceRefresh);

      var periods = result.Periods
        .Where(p => !p.IsPlaceholder && MalaysiaTime.Convert(p.Time).Date == date.Date)
        .OrderBy(p => p.Time)
        .ToList();

      return result with { Periods = periods };
    }

    private async Task<WeatherProviderResult> FetchRealtimeSliceAsync(
      IWeatherProviderAdapter adapter,
      Uri uri,
      bool forceRefresh,
      CancellationToken cancellationToken)
    {
      var result = await FetchProviderDataAsync(adapter, uri, "Realtime", cancellationToken, forceRefresh);

      if (result.Periods.Count > 1)
      {
        var now = MalaysiaTime.Now;
        var closest = result.Periods.MinBy(p => Math.Abs((p.Time - now).Ticks));
        if (closest != null)
        {
          result = result with { Periods = [closest] };
        }
      }

      return result;
    }

    private async Task<WeatherProviderResult> FetchOutlookSlicesAsync(
      IWeatherProviderAdapter adapter,
      Uri uri,
      int maxDays,
      bool forceRefresh,
      CancellationToken cancellationToken)
    {
      var result = await FetchProviderDataAsync(adapter, uri, "Outlook", cancellationToken, forceRefresh);

      if (result.Periods.Count > 0)
      {
        var today = MalaysiaTime.Today;
        var allowedDates = result.Periods
          .Select(p => MalaysiaTime.Convert(p.Time).Date)
          .Where(date => date >= today)
          .Distinct()
          .OrderBy(date => date)
          .Take(maxDays)
          .ToHashSet();

        var sorted = result.Periods
          .Where(p => allowedDates.Contains(MalaysiaTime.Convert(p.Time).Date))
          .OrderBy(p => p.Time)
          .ToList();
        result = result with { Periods = sorted };
      }

      return result;
    }

    private async Task<WeatherProviderResult> FetchProviderDataAsync(
      IWeatherProviderAdapter adapter,
      Uri endpoint,
      string label,
      CancellationToken cancellationToken = default,
      bool forceRefresh = false)
    {
      var fallback = CreatePlaceholder();
      if (!adapter.Options.Enabled)
      {
        return BuildSample(adapter, "Disabled", WeatherBadgeTone.Muted, fallback, endpoint);
      }

      var isOutlook = IsOutlook(label);
      string cacheKey = $"weather_api_{(isOutlook ? "outlook" : "current")}_{endpoint.AbsoluteUri}";

      if (forceRefresh)
      {
        _cache.Remove(cacheKey);
      }

      try
      {
        var wasCached = _cache.TryGetValue(cacheKey, out Lazy<Task<WeatherProviderResult>>? cachedTask);
        var lazyTask = cachedTask ?? _cache.GetOrCreate(cacheKey, entry =>
        {
          entry.Size = 1;
          entry.AbsoluteExpirationRelativeToNow = isOutlook
            ? _outlookCacheExpiration
            : _liveCacheExpiration;
          return new Lazy<Task<WeatherProviderResult>>(
            () => FetchProviderDataCoreAsync(adapter, endpoint, label, fallback),
            LazyThreadSafetyMode.ExecutionAndPublication);
        })!;

        var cachedResult = await lazyTask.Value.WaitAsync(cancellationToken);

        if (cachedResult.Tone != WeatherBadgeTone.Live)
        {
          // MemoryCacheEntryOptions cannot be changed after the factory returns.
          // Replace the entry explicitly so transient failures use the shorter fallback lifetime.
          _cache.Set(cacheKey, lazyTask, new MemoryCacheEntryOptions
          {
            AbsoluteExpirationRelativeToNow = _fallbackCacheExpiration,
            Size = 1
          });
          return wasCached
            ? cachedResult with { Status = $"{cachedResult.Status} (Cached)", IsCached = true }
            : cachedResult;
        }

        var status = isOutlook ? "FORECAST" : "LIVE";
        if (wasCached) status += " (Cached)";
        return cachedResult with { Status = status, IsCached = wasCached };
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        // The caller only cancelled its wait; the shared request remains useful to
        // other callers and is still bounded by HttpClient.Timeout.
        throw;
      }
      catch (Exception)
      {
        _cache.Remove(cacheKey);
        return BuildSample(adapter, "Unavailable", WeatherBadgeTone.Error, fallback, endpoint);
      }
    }

    private static TimeSpan GetPositiveDuration(int seconds, string name) =>
      seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : throw new InvalidOperationException($"Cache expiration setting '{name}' must be greater than zero.");

    private static bool IsOutlook(string label) =>
      string.Equals(label, "Outlook", StringComparison.OrdinalIgnoreCase);

    private async Task<WeatherProviderResult> FetchProviderDataCoreAsync(
      IWeatherProviderAdapter adapter,
      Uri endpoint,
      string label,
      IReadOnlyList<WeatherTimeSlice> fallback)
    {
      var provider = adapter.Options;
      var requestTimeout = _httpClient.Timeout == Timeout.InfiniteTimeSpan
        ? TimeSpan.FromSeconds(15)
        : _httpClient.Timeout;
      using var timeoutCts = new CancellationTokenSource(requestTimeout);
      var requestToken = timeoutCts.Token;
      using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
      if (!string.IsNullOrWhiteSpace(provider.ApiKey))
      {
        request.Headers.TryAddWithoutValidation("Authorization", provider.ApiKey);
      }

      // The shared request is bounded by HttpClient.Timeout. Individual callers can
      // cancel their wait without cancelling the request for every coalesced caller.
      using var response = await _httpClient.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        requestToken);

      if (!response.IsSuccessStatusCode)
      {
        return BuildSample(adapter, $"HTTP {(int)response.StatusCode}", WeatherBadgeTone.Error, fallback, endpoint);
      }

      await using var payload = await response.Content.ReadAsStreamAsync(requestToken);
      using var document = await JsonDocument.ParseAsync(payload, cancellationToken: requestToken);

      return new WeatherProviderResult
      {
        Provider = adapter.Name,
        Source = provider.Source,
        Status = "LIVE",
        Tone = WeatherBadgeTone.Live,
        RetrievedAt = DateTimeOffset.UtcNow,
        Periods = adapter.ParseSlices(document, fallback, label),
        Endpoint = endpoint
      };
    }

    private static IReadOnlyList<WeatherTimeSlice> CreatePlaceholder() =>
    [
        new()
        {
            Label = "N/A",
            Time = DateTimeOffset.UtcNow,
            Summary = "Pending API data.",
            IsPlaceholder = true
        }
    ];

    private static WeatherProviderResult BuildSample(
      IWeatherProviderAdapter adapter,
      string status,
      WeatherBadgeTone tone,
      IReadOnlyList<WeatherTimeSlice> slices,
      Uri? endpoint = null) => new()
      {
        Provider = adapter.Name,
        Source = adapter.Options.Source ?? "Unknown",
        Status = status,
        Tone = tone,
        RetrievedAt = DateTimeOffset.UtcNow,
        Periods = slices,
        Endpoint = endpoint
      };
  }
}
