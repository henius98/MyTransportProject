using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Services
{
  public class WeatherPlannerService : IWeatherPlannerService
  {

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _liveCacheExpiration;
    private readonly TimeSpan _outlookCacheExpiration;
    private readonly TimeSpan _fallbackCacheExpiration;

    public WeatherPlannerService(
      HttpClient httpClient,
      IMemoryCache cache,
      IOptions<CacheExpirationOptions> cacheExpiration)
    {
      _httpClient = httpClient;
      _cache = cache;
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


    public async Task<WeatherProviderResult> FetchProviderDataAsync(
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
          _cache.Set(cacheKey, lazyTask, _fallbackCacheExpiration);
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
