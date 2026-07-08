using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Services
{
  public class WeatherPlannerService : IWeatherPlannerService
  {

    private readonly HttpClient _httpClient;
    private readonly WeatherOptions _options;
    private readonly IMemoryCache _cache;

    public WeatherPlannerService(HttpClient httpClient, IOptions<WeatherOptions> options, IMemoryCache cache)
    {
      _httpClient = httpClient;
      _options = options.Value;
      _cache = cache;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores = new();

    public async Task<WeatherProviderResult> FetchProviderDataAsync(WeatherProviderOptions provider, Uri endpoint, string label, CancellationToken cancellationToken = default)
    {
      var fallback = CreatePlaceholder();
      if (!provider.Enabled) return BuildSample(provider, "Disabled", fallback, endpoint);

      string cacheKey = $"weather_api_{endpoint.AbsoluteUri}";
      if (_cache.TryGetValue(cacheKey, out WeatherProviderResult? cachedResult) && cachedResult != null)
      {
         return cachedResult with { Status = "Live (Cached)", Periods = cachedResult.Periods.Select(p => p with { Label = label }).ToList() };
      }

      try
      {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
          request.Headers.TryAddWithoutValidation("Authorization", provider.ApiKey);

        var semaphore = _hostSemaphores.GetOrAdd(endpoint.Host, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        HttpResponseMessage response;
        try
        {
            // Double-check cache inside the lock to prevent cache stampede
            if (_cache.TryGetValue(cacheKey, out WeatherProviderResult? doubleCachedResult) && doubleCachedResult != null)
            {
                return doubleCachedResult with { Status = "Live (Cached)", Periods = doubleCachedResult.Periods.Select(p => p with { Label = label }).ToList() };
            }

            response = await _httpClient.SendAsync(request, cancellationToken);
            // Add a small delay to space out sequential requests to the same host (especially data.gov.my)
            await Task.Delay(500, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }

        if (!response.IsSuccessStatusCode)
        {
          var failedResult = BuildSample(provider, $"HTTP {(int)response.StatusCode}", fallback, endpoint);
          // Cache the failure for a short time to prevent retry storms on rate limits
          _cache.Set(cacheKey, failedResult, TimeSpan.FromSeconds(30));
          return failedResult;
        }

        await using var payload = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);

        var result = new WeatherProviderResult
        {
          Provider = provider.Name,
          Source = provider.Source,
          Status = "Live",
          RetrievedAt = DateTimeOffset.UtcNow,
          Periods = WeatherShaper.ShapeSlices(document, fallback, label),
          Endpoint = endpoint
        };

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
        return result;
      }
      catch (Exception)
      {
        return BuildSample(provider, "Unavailable", fallback, endpoint);
      }
    }

    private static IReadOnlyList<WeatherTimeSlice> CreatePlaceholder() => new List<WeatherTimeSlice>
    {
        new()
        {
            Label = "N/A",
            Time = DateTimeOffset.UtcNow,
            Summary = "Pending API data.",
            IsPlaceholder = true
        }
    };

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
