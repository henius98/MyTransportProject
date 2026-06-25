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

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
          return BuildSample(provider, $"HTTP {(int)response.StatusCode}", fallback, endpoint);

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
