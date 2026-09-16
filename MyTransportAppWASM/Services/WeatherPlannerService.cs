using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace MyTransportAppWASM.Services
{
  public class WeatherPlannerService : IWeatherPlannerService
  {

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;

    public WeatherPlannerService(HttpClient httpClient, IMemoryCache cache)
    {
      _httpClient = httpClient;
      _cache = cache;
    }


    public async Task<WeatherProviderResult> FetchProviderDataAsync(WeatherProviderOptions provider, Uri endpoint, string label, CancellationToken cancellationToken = default)
    {
      var fallback = CreatePlaceholder();
      if (!provider.Enabled) return BuildSample(provider, "Disabled", fallback, endpoint);

      string cacheKey = $"weather_api_{endpoint.AbsoluteUri}";

      try
      {
        var lazyTask = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return new Lazy<Task<WeatherProviderResult>>(
              () => FetchProviderDataCoreAsync(provider, endpoint, label, fallback),
              LazyThreadSafetyMode.ExecutionAndPublication);
        });

        var cachedResult = await lazyTask!.Value.WaitAsync(cancellationToken);

        if (!string.Equals(cachedResult.Status, "Live", StringComparison.Ordinal))
        {
          // MemoryCacheEntryOptions cannot be changed after the factory returns.
          // Replace the entry explicitly so transient failures do not persist for 10 minutes.
          _cache.Set(cacheKey, lazyTask, TimeSpan.FromSeconds(30));
          return RelabelIfRequired(cachedResult, label);
        }

        var status = (DateTimeOffset.UtcNow - cachedResult.RetrievedAt).TotalSeconds > 2
          ? "Live (Cached)"
          : "Live";
        var labeledResult = RelabelIfRequired(cachedResult, label);
        return string.Equals(labeledResult.Status, status, StringComparison.Ordinal)
          ? labeledResult
          : labeledResult with { Status = status };
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
        Status = "Live",
        RetrievedAt = DateTimeOffset.UtcNow,
        Periods = WeatherShaper.ShapeSlices(document, fallback, label),
        Endpoint = endpoint
      };
    }

    private static WeatherProviderResult RelabelIfRequired(WeatherProviderResult result, string label)
    {
      for (var index = 0; index < result.Periods.Count; index++)
      {
        if (!string.Equals(result.Periods[index].Label, label, StringComparison.Ordinal))
        {
          var relabeledPeriods = new List<WeatherTimeSlice>(result.Periods.Count);
          foreach (var period in result.Periods)
          {
            relabeledPeriods.Add(period with { Label = label });
          }

          return result with { Periods = relabeledPeriods };
        }
      }

      return result;
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
