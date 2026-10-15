using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Services
{
  public class WeatherPlannerService : IWeatherPlannerService
  {
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly WeatherOptions _options;
    private readonly IPlaceResolutionService? _placeResolutionService;
    private readonly IWeatherStationCatalogService? _stationCatalog;
    private readonly TimeSpan _liveCacheExpiration;
    private readonly TimeSpan _outlookCacheExpiration;
    private readonly TimeSpan _fallbackCacheExpiration;

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
      _options = weatherOptions.Value ?? new();
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
    }

    public WeatherPlannerService(
      HttpClient httpClient,
      IMemoryCache cache,
      IOptions<CacheExpirationOptions> cacheExpiration)
      : this(httpClient, cache, cacheExpiration, Options.Create(new WeatherOptions()), null, null)
    {
    }

    public async Task<WeatherPlanResult> GetPlanAsync(
      WeatherQuery query,
      CancellationToken cancellationToken = default,
      bool forceRefresh = false)
    {
      ArgumentNullException.ThrowIfNull(query);

      var hasPendingSearch = !string.IsNullOrWhiteSpace(query.PendingSearchText);
      if (!hasPendingSearch && (!double.IsFinite(query.Latitude) || !double.IsFinite(query.Longitude)
        || query.Latitude is < -90 or > 90 || query.Longitude is < -180 or > 180))
      {
        return InvalidPlan(query, "Enter a valid latitude (-90 to 90) and longitude (-180 to 180).");
      }

      var minDate = MalaysiaTime.Today;
      var maxDate = minDate.AddDays(WeatherForecastLimits.OpenMeteoForecastDays - 1);
      var departDate = query.Departure.HasValue ? MalaysiaTime.Convert(query.Departure.Value).Date : (DateTime?)null;
      var returnDate = query.Return.HasValue ? MalaysiaTime.Convert(query.Return.Value).Date : (DateTime?)null;

      if ((departDate.HasValue && (departDate.Value < minDate || departDate.Value > maxDate))
        || (returnDate.HasValue && (returnDate.Value < minDate || returnDate.Value > maxDate)))
      {
        return InvalidPlan(
          query,
          $"Select dates within the {WeatherForecastLimits.OpenMeteoForecastDays}-day forecast window ending {maxDate:dd MMM yyyy}.");
      }

      if (departDate.HasValue && returnDate.HasValue && returnDate.Value < departDate.Value)
      {
        return InvalidPlan(query, "Return date must be on or after the departure date.");
      }

      if (hasPendingSearch)
      {
        var searchText = query.PendingSearchText!.Trim();
        var suggestions = _placeResolutionService != null
          ? await _placeResolutionService.GetSuggestionsAsync(query.PendingSearchText!)
          : [];
        var exactMatches = suggestions.Where(suggestion =>
          string.Equals(suggestion.Name, searchText, StringComparison.OrdinalIgnoreCase)
          || string.Equals(suggestion.FormattedAddress, $"MET ID: {searchText}", StringComparison.OrdinalIgnoreCase)).ToList();

        (double Lat, double Lng, string FormattedAddress, string Name) chosen;
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
          return InvalidPlan(query, error);
        }

        query.PlaceName = chosen.Name;
        query.Latitude = chosen.Lat;
        query.Longitude = chosen.Lng;
        query.PendingSearchText = null;
        query.LocationId = chosen.FormattedAddress != null && chosen.FormattedAddress.StartsWith("MET ID: ", StringComparison.Ordinal)
          ? chosen.FormattedAddress.Substring(8).Trim()
          : null;
      }

      if (string.IsNullOrWhiteSpace(query.LocationId))
      {
        await ResolveNearestLocationAsync(query, updatePlaceName: false, cancellationToken);
      }

      var realtimeTasks = new List<Task<WeatherProviderResult>>();
      var outlookTasks = new List<Task<WeatherProviderResult>>();

      // 1. MetMalaysia
      if (_options.MetMalaysia.Enabled && !string.IsNullOrWhiteSpace(query.LocationId))
      {
        realtimeTasks.Add(FetchMetMalaysiaRealtimeAsync(query, forceRefresh, cancellationToken));
        if (query.Return.HasValue)
        {
          outlookTasks.Add(FetchMetMalaysiaOutlookAsync(query, forceRefresh, cancellationToken));
        }
      }

      // 2. Open-Meteo
      if (_options.OpenMeteo.Enabled)
      {
        realtimeTasks.Add(FetchOpenMeteoRealtimeAsync(query, forceRefresh, cancellationToken));
        if (query.Return.HasValue)
        {
          outlookTasks.Add(FetchOpenMeteoOutlookAsync(query, forceRefresh, cancellationToken));
        }
      }

      // 3. Singapore NEA
      if (_options.SingaporeNEA.Enabled && IsNearSingapore(query.Latitude, query.Longitude))
      {
        realtimeTasks.Add(FetchSingaporeNeaRealtimeAsync(query, forceRefresh, cancellationToken));
        if (query.Return.HasValue)
        {
          outlookTasks.Add(FetchSingaporeNeaOutlookAsync(query, forceRefresh, cancellationToken));
        }
      }

      await Task.WhenAll(realtimeTasks.Concat(outlookTasks));

      var realtimeResults = new List<WeatherProviderResult>();
      foreach (var task in realtimeTasks)
      {
        var result = await task;
        if (result != null) realtimeResults.Add(result);
      }

      var outlookResults = new List<WeatherProviderResult>();
      foreach (var task in outlookTasks)
      {
        var result = await task;
        if (result != null) outlookResults.Add(result);
      }

      return new WeatherPlanResult
      {
        Query = query with { },
        Realtime = realtimeResults,
        Outlooks = outlookResults
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

      if (string.Equals(providerName, "MetMalaysia", StringComparison.OrdinalIgnoreCase))
      {
        return isOutlook
          ? await FetchMetMalaysiaOutlookAsync(query, forceRefresh: true, cancellationToken)
          : await FetchMetMalaysiaRealtimeAsync(query, forceRefresh: true, cancellationToken);
      }

      if (string.Equals(providerName, "Open-Meteo", StringComparison.OrdinalIgnoreCase))
      {
        return isOutlook
          ? await FetchOpenMeteoOutlookAsync(query, forceRefresh: true, cancellationToken)
          : await FetchOpenMeteoRealtimeAsync(query, forceRefresh: true, cancellationToken);
      }

      if (string.Equals(providerName, "Singapore NEA", StringComparison.OrdinalIgnoreCase))
      {
        return isOutlook
          ? await FetchSingaporeNeaOutlookAsync(query, forceRefresh: true, cancellationToken)
          : await FetchSingaporeNeaRealtimeAsync(query, forceRefresh: true, cancellationToken);
      }

      throw new ArgumentException($"Unknown weather provider '{providerName}'", nameof(providerName));
    }

    public async Task<WeatherProviderResult> GetHourlyForecastAsync(
      WeatherQuery query,
      DateTime date,
      CancellationToken cancellationToken = default,
      bool forceRefresh = false)
    {
      ArgumentNullException.ThrowIfNull(query);

      if (!_options.OpenMeteo.Endpoints.TryGetValue("Hourly", out var hourlyPath))
      {
        return BuildSample(_options.OpenMeteo, "Unavailable", Array.Empty<WeatherTimeSlice>());
      }

      var template = (_options.OpenMeteo.BaseUrl + hourlyPath)
        .Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{timezone}", Uri.EscapeDataString(MalaysiaTime.IanaTimeZone), StringComparison.OrdinalIgnoreCase);

      var uri = BuildOpenMeteoUri(template, _options.OpenMeteo, query);
      var result = await FetchProviderDataAsync(_options.OpenMeteo, uri, "Outlook", cancellationToken, forceRefresh);

      var periods = result.Periods
        .Where(p => !p.IsPlaceholder && MalaysiaTime.Convert(p.Time).Date == date.Date)
        .OrderBy(p => p.Time)
        .ToList();

      return result with { Periods = periods };
    }

    private Task<WeatherProviderResult> FetchMetMalaysiaRealtimeAsync(
      WeatherQuery query,
      bool forceRefresh,
      CancellationToken cancellationToken) =>
      FetchRealtimeSliceAsync(
        _options.MetMalaysia,
        BuildMetMalaysiaUri(_options.MetMalaysia, query, isOutlook: false),
        forceRefresh,
        cancellationToken);

    private Task<WeatherProviderResult> FetchMetMalaysiaOutlookAsync(
      WeatherQuery query,
      bool forceRefresh,
      CancellationToken cancellationToken) =>
      FetchOutlookSlicesAsync(
        _options.MetMalaysia,
        BuildMetMalaysiaUri(_options.MetMalaysia, query, isOutlook: true),
        maxDays: 14,
        forceRefresh,
        cancellationToken);

    private Task<WeatherProviderResult> FetchOpenMeteoRealtimeAsync(
      WeatherQuery query,
      bool forceRefresh,
      CancellationToken cancellationToken)
    {
      string endpointPath = _options.OpenMeteo.Endpoints.TryGetValue("Realtime", out var path) ? path : string.Empty;
      var uri = BuildOpenMeteoUri(_options.OpenMeteo.BaseUrl + endpointPath, _options.OpenMeteo, query);
      return FetchRealtimeSliceAsync(_options.OpenMeteo, uri, forceRefresh, cancellationToken);
    }

    private Task<WeatherProviderResult> FetchOpenMeteoOutlookAsync(
      WeatherQuery query,
      bool forceRefresh,
      CancellationToken cancellationToken)
    {
      string endpointPath = _options.OpenMeteo.Endpoints.TryGetValue("Outlook", out var path) ? path : string.Empty;
      var uri = BuildOpenMeteoUri(_options.OpenMeteo.BaseUrl + endpointPath, _options.OpenMeteo, query);
      return FetchOutlookSlicesAsync(
        _options.OpenMeteo,
        uri,
        WeatherForecastLimits.OpenMeteoForecastDays,
        forceRefresh,
        cancellationToken);
    }

    private Task<WeatherProviderResult> FetchSingaporeNeaRealtimeAsync(
      WeatherQuery query,
      bool forceRefresh,
      CancellationToken cancellationToken)
    {
      string endpointPath = _options.SingaporeNEA.Endpoints.TryGetValue("Realtime", out var path) ? path : string.Empty;
      var uri = BuildSingaporeNeaUri(_options.SingaporeNEA.BaseUrl + endpointPath, _options.SingaporeNEA, query);
      return FetchRealtimeSliceAsync(_options.SingaporeNEA, uri, forceRefresh, cancellationToken);
    }

    private Task<WeatherProviderResult> FetchSingaporeNeaOutlookAsync(
      WeatherQuery query,
      bool forceRefresh,
      CancellationToken cancellationToken)
    {
      string endpointPath = _options.SingaporeNEA.Endpoints.TryGetValue("Outlook", out var path) ? path : string.Empty;
      var uri = BuildSingaporeNeaUri(_options.SingaporeNEA.BaseUrl + endpointPath, _options.SingaporeNEA, query);
      return FetchOutlookSlicesAsync(_options.SingaporeNEA, uri, maxDays: 4, forceRefresh, cancellationToken);
    }

    private async Task<WeatherProviderResult> FetchRealtimeSliceAsync(
      WeatherProviderOptions provider,
      Uri uri,
      bool forceRefresh,
      CancellationToken cancellationToken)
    {
      var result = await FetchProviderDataAsync(provider, uri, "Realtime", cancellationToken, forceRefresh);

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
      WeatherProviderOptions provider,
      Uri uri,
      int maxDays,
      bool forceRefresh,
      CancellationToken cancellationToken)
    {
      var result = await FetchProviderDataAsync(provider, uri, "Outlook", cancellationToken, forceRefresh);

      if (result.Periods.Count > 0)
      {
        var now = MalaysiaTime.Now;
        var sorted = result.Periods
          .Where(p => MalaysiaTime.Convert(p.Time).Date >= now.Date)
          .OrderBy(p => p.Time)
          .Take(maxDays)
          .ToList();
        result = result with { Periods = sorted };
      }

      return result;
    }

    private static Uri BuildSingaporeNeaUri(string template, KeyedWeatherProviderOptions options, WeatherQuery query)
    {
      var formatted = template
        .Replace("{lat}", query.Latitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{lon}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

      if (formatted.Contains("{place}", StringComparison.OrdinalIgnoreCase))
      {
        string placeStr = string.IsNullOrWhiteSpace(query.PlaceName) || query.PlaceName == "Current Location"
          ? "Kuala Lumpur"
          : query.PlaceName.Split(',')[0].Trim();
        formatted = formatted.Replace("{place}", Uri.EscapeDataString(placeStr), StringComparison.OrdinalIgnoreCase);
      }

      if (!string.IsNullOrWhiteSpace(options.ApiKey))
      {
        formatted = formatted
          .Replace("{key}", options.ApiKey, StringComparison.OrdinalIgnoreCase)
          .Replace("{apiKey}", options.ApiKey, StringComparison.OrdinalIgnoreCase);
      }

      return new Uri(formatted, UriKind.Absolute);
    }

    private static Uri BuildMetMalaysiaUri(MetMalaysiaWeatherProviderOptions options, WeatherQuery query, bool isOutlook)
    {
      var template = options.BaseUrl + options.Endpoints;

      if (template.Contains("{location_id}", StringComparison.OrdinalIgnoreCase))
      {
        string locId = !string.IsNullOrWhiteSpace(query.LocationId) ? query.LocationId : "Ds058";
        template = template.Replace("{location_id}", Uri.EscapeDataString(locId), StringComparison.OrdinalIgnoreCase);
      }

      if (template.Contains("{date_start}", StringComparison.OrdinalIgnoreCase) ||
          template.Contains("{date_end}", StringComparison.OrdinalIgnoreCase))
      {
        var currentMalaysiaTime = MalaysiaTime.Now;
        var start = isOutlook ? query.Departure ?? currentMalaysiaTime : currentMalaysiaTime;
        var end = isOutlook ? query.Return ?? start.AddDays(7) : currentMalaysiaTime;
        template = template.Replace("{date_start}", start.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase);
        template = template.Replace("{date_end}", end.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase);
      }

      if (!string.IsNullOrWhiteSpace(options.ApiKey))
      {
        template = template
          .Replace("{key}", options.ApiKey, StringComparison.OrdinalIgnoreCase)
          .Replace("{apiKey}", options.ApiKey, StringComparison.OrdinalIgnoreCase);
      }

      return new Uri(template, UriKind.Absolute);
    }

    private static Uri BuildOpenMeteoUri(string template, KeyedWeatherProviderOptions options, WeatherQuery query)
    {
      var formatted = template
        .Replace("{lat}", query.Latitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{lon}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

      if (formatted.Contains("{forecast_hours}", StringComparison.OrdinalIgnoreCase))
      {
        formatted = formatted.Replace("{forecast_hours}", "1", StringComparison.OrdinalIgnoreCase);
      }

      if (formatted.Contains("{forecast_days}", StringComparison.OrdinalIgnoreCase))
      {
        int days = 7;
        if (query.Return.HasValue)
        {
          var timeSpan = MalaysiaTime.Convert(query.Return.Value).Date - MalaysiaTime.Today;
          days = Math.Max(1, Math.Min(WeatherForecastLimits.OpenMeteoForecastDays, timeSpan.Days + 2));
        }
        formatted = formatted.Replace("{forecast_days}", days.ToString(), StringComparison.OrdinalIgnoreCase);
      }

      if (!string.IsNullOrWhiteSpace(options.ApiKey))
      {
        formatted = formatted
          .Replace("{key}", options.ApiKey, StringComparison.OrdinalIgnoreCase)
          .Replace("{apiKey}", options.ApiKey, StringComparison.OrdinalIgnoreCase);
      }

      return new Uri(formatted, UriKind.Absolute);
    }

    public static bool IsNearSingapore(double lat, double lon) =>
      lat >= 1.15 && lat <= 1.50 && lon >= 103.55 && lon <= 104.15;

    private async Task<WeatherProviderResult> FetchProviderDataAsync(
      WeatherProviderOptions provider,
      Uri endpoint,
      string label,
      CancellationToken cancellationToken = default,
      bool forceRefresh = false)
    {
      var fallback = CreatePlaceholder();
      if (!provider.Enabled) return BuildSample(provider, "Disabled", fallback, endpoint);

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
            () => FetchProviderDataCoreAsync(provider, endpoint, label, fallback),
            LazyThreadSafetyMode.ExecutionAndPublication);
        })!;

        var cachedResult = await lazyTask.Value.WaitAsync(cancellationToken);
        var labeledResult = RelabelIfRequired(cachedResult, label);

        if (!string.Equals(cachedResult.Status, "LIVE", StringComparison.Ordinal))
        {
          // MemoryCacheEntryOptions cannot be changed after the factory returns.
          // Replace the entry explicitly so transient failures use the shorter fallback lifetime.
          _cache.Set(cacheKey, lazyTask, new MemoryCacheEntryOptions
          {
            AbsoluteExpirationRelativeToNow = _fallbackCacheExpiration,
            Size = 1
          });
          return wasCached
            ? labeledResult with { Status = $"{labeledResult.Status} (Cached)", IsCached = true }
            : labeledResult;
        }

        var status = GetStatus(label, endpoint);
        if (wasCached) status += " (Cached)";
        return labeledResult with { Status = status, IsCached = wasCached };
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
        return BuildSample(provider, "Unavailable", fallback, endpoint);
      }
    }

    private static TimeSpan GetPositiveDuration(int seconds, string name) =>
      seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : throw new InvalidOperationException($"Cache expiration setting '{name}' must be greater than zero.");

    private static bool IsOutlook(string label) =>
      string.Equals(label, "Outlook", StringComparison.OrdinalIgnoreCase);

    private async Task<WeatherProviderResult> FetchProviderDataCoreAsync(
      WeatherProviderOptions provider,
      Uri endpoint,
      string label,
      IReadOnlyList<WeatherTimeSlice> fallback)
    {
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
        return BuildSample(provider, $"HTTP {(int)response.StatusCode}", fallback, endpoint);
      }

      await using var payload = await response.Content.ReadAsStreamAsync(requestToken);
      using var document = await JsonDocument.ParseAsync(payload, cancellationToken: requestToken);

      return new WeatherProviderResult
      {
        Provider = provider.Name,
        Source = provider.Source,
        Status = "LIVE",
        RetrievedAt = DateTimeOffset.UtcNow,
        Periods = WeatherShaper.ShapeSlices(document, fallback, label),
        Endpoint = endpoint
      };
    }

    private static string GetStatus(string label, Uri endpoint)
    {
      if (IsOutlook(label)) return "FORECAST";
      if (label.Contains("real", StringComparison.OrdinalIgnoreCase) ||
          endpoint.AbsoluteUri.Contains("real-time", StringComparison.OrdinalIgnoreCase) ||
          endpoint.Query.Contains("current_weather", StringComparison.OrdinalIgnoreCase)) return "LIVE";
      return "TODAY";
    }

    private static WeatherProviderResult RelabelIfRequired(WeatherProviderResult result, string label)
    {
      for (var index = 0; index < result.Periods.Count; index++)
      {
        if (IsProviderLabel(result.Periods[index].Label) &&
            !string.Equals(result.Periods[index].Label, label, StringComparison.Ordinal))
        {
          var relabeledPeriods = new List<WeatherTimeSlice>(result.Periods.Count);
          foreach (var period in result.Periods)
          {
            relabeledPeriods.Add(IsProviderLabel(period.Label)
              ? period with { Label = label }
              : period);
          }

          return result with { Periods = relabeledPeriods };
        }
      }

      return result;
    }

    private static bool IsProviderLabel(string label) =>
      string.Equals(label, "Realtime", StringComparison.OrdinalIgnoreCase) ||
      string.Equals(label, "Outlook", StringComparison.OrdinalIgnoreCase);

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

    private static WeatherProviderResult BuildSample(WeatherProviderOptions provider, string status, IReadOnlyList<WeatherTimeSlice> slices, Uri? endpoint = null) => new()
    {
      Provider = provider.Name ?? "Unknown",
      Source = provider.Source ?? "Unknown",
      Status = status,
      RetrievedAt = DateTimeOffset.UtcNow,
      Periods = slices,
      Endpoint = endpoint
    };
  }
}
