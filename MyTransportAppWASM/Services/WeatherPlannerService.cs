using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MyTransportAppWASM.Models.Weather;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Services
{
  public class WeatherPlannerService : IWeatherPlannerService
  {
    private const string UserAgent = "MyTransportApp/1.0 (weather planner)";
    
    private readonly HttpClient _httpClient;
    private readonly WeatherOptions _options;
    private readonly IMemoryCache _cache;

    public WeatherPlannerService(HttpClient httpClient, IOptions<WeatherOptions> options, IMemoryCache cache)
    {
      _httpClient = httpClient;
      _options = options.Value;
      _cache = cache;
    }

    public async Task<WeatherPlanResult> GetWeatherPlanAsync(WeatherQuery query, CancellationToken cancellationToken = default)
    {
      // Cache key based on rounded coordinates (approx 1.1km grid)
      string cacheKey = $"weather_{Math.Round(query.Latitude, 2)}_{Math.Round(query.Longitude, 2)}";
      if (_cache.TryGetValue(cacheKey, out WeatherPlanResult? cachedResult) && cachedResult != null)
      {
          return cachedResult with { Query = query, Notes = cachedResult.Notes.Append("Restored from local cache.").ToList() };
      }

      try
      {
        Task<WeatherProviderResult> baselineTask = IsNearSingapore(query.Latitude, query.Longitude)
          ? FetchSingaporeNEAAsync(query, "Baseline", "Baseline (NEA)", cancellationToken)
          : FetchMetMalaysiaAsync(query, cancellationToken);

        Task<WeatherProviderResult> radarTask = FetchRadarAsync(query, cancellationToken);
        Task<WeatherProviderResult?> weatherbitTask = FetchWeatherbitAsync(query, cancellationToken);
        Task<IReadOnlyList<WeatherProviderResult>> outlooksTask = FetchOutlooksAsync(query, cancellationToken);

        await Task.WhenAll(baselineTask, radarTask, weatherbitTask, outlooksTask);

        var outlooks = (await outlooksTask).ToList();
        var weatherbit = await weatherbitTask;
        if (weatherbit != null) outlooks.Add(weatherbit);

        var result = new WeatherPlanResult
        {
          Query = query,
          Baseline = await baselineTask,
          Radar = await radarTask,
          Outlooks = outlooks,
          Notes = new List<string>
          {
            "CORS-enabled providers like Open-Meteo are active for live data.",
            $"Request: {query.PlaceName ?? "Custom location"} ({query.Latitude:F3}, {query.Longitude:F3})."
          }
        };

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
        return result;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Global weather planner failure: {ex.Message}");
        return new WeatherPlanResult
        {
          Query = query,
          Baseline = BuildSample(_options.MetMalaysia, query, "Error", ex.Message, WeatherSampleData.SampleBaseline(query)),
          Radar = BuildSample(_options.Radar, query, "Error", ex.Message, WeatherSampleData.SampleRadar(query)),
          Outlooks = new List<WeatherProviderResult>(),
          Notes = new List<string> { $"Error: {ex.Message}" }
        };
      }
    }

    private async Task<WeatherProviderResult?> FetchWeatherbitAsync(WeatherQuery query, CancellationToken cancellationToken)
    {
      var provider = _options.Weatherbit;
      if (!provider.Enabled) return null;

      var updatedProvider = provider with { }; // Clone
      if (updatedProvider.Suffixes.TryGetValue("Baseline", out string? suffix))
      {
         updatedProvider.Endpoint = (updatedProvider.Endpoint ?? "").TrimEnd('/') + "/" + suffix;
      }

      return await FetchAndShapeAsync(updatedProvider, query, "Weatherbit", WeatherSampleData.SampleBaseline(query), cancellationToken);
    }

    private static bool IsNearSingapore(double lat, double lon) => 
        lat >= 1.15 && lat <= 1.50 && lon >= 103.55 && lon <= 104.15;

    private async Task<WeatherProviderResult> FetchSingaporeNEAAsync(WeatherQuery query, string suffixKey, string label, CancellationToken cancellationToken)
    {
      var provider = _options.SingaporeNEA;
      var fallback = WeatherSampleData.SampleBaseline(query);

      if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.Endpoint))
        return BuildSample(provider, query, "Not configured", "NEA provider disabled.", fallback);

      var updatedProvider = provider with { };
      if (updatedProvider.Suffixes.TryGetValue(suffixKey, out string? suffix))
         updatedProvider.Endpoint = updatedProvider.Endpoint.TrimEnd('/') + "/" + suffix;

      return await FetchAndShapeAsync(updatedProvider, query, label, fallback, cancellationToken);
    }

    private async Task<WeatherProviderResult> FetchMetMalaysiaAsync(WeatherQuery query, CancellationToken cancellationToken)
    {
      var provider = _options.MetMalaysia;
      var fallback = WeatherSampleData.SampleBaseline(query);

      if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.Endpoint))
        return BuildSample(provider, query, "Not configured", "MetMalaysia provider disabled.", fallback);

      return await FetchAndShapeAsync(provider, query, "Baseline", fallback, cancellationToken);
    }

    private async Task<WeatherProviderResult> FetchRadarAsync(WeatherQuery query, CancellationToken cancellationToken)
    {
      var provider = _options.Radar;
      var fallback = WeatherSampleData.SampleRadar(query);

      if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.Endpoint))
        return BuildSample(provider, query, "Not configured", "Radar provider disabled.", fallback);

      return await FetchAndShapeAsync(provider, query, "Radar", fallback, cancellationToken);
    }

    private async Task<IReadOnlyList<WeatherProviderResult>> FetchOutlooksAsync(WeatherQuery query, CancellationToken cancellationToken)
    {
      var outlookProviders = _options.Outlooks.ToList();

      if (IsNearSingapore(query.Latitude, query.Longitude))
      {
         var nea = _options.SingaporeNEA;
         if (nea.Enabled && nea.Suffixes.TryGetValue("Outlook", out string? outlookSuffix))
         {
             outlookProviders.Insert(0, new WeatherProviderOptions 
             { 
               Name = "Singapore NEA Outlook",
               Source = nea.Source,
               Endpoint = nea.Endpoint?.TrimEnd('/') + "/" + outlookSuffix,
               Enabled = true
             });
         }
      }

      var tasks = outlookProviders.Select(provider =>
      {
        var fallback = WeatherSampleData.SampleOutlook(query);
        return (!provider.Enabled || string.IsNullOrWhiteSpace(provider.Endpoint))
          ? Task.FromResult(BuildSample(provider, query, "Not configured", "Provider disabled.", fallback))
          : FetchAndShapeAsync(provider, query, "Outlook", fallback, cancellationToken);
      });

      return await Task.WhenAll(tasks);
    }

    private async Task<WeatherProviderResult> FetchAndShapeAsync(
      WeatherProviderOptions provider,
      WeatherQuery query,
      string label,
      IReadOnlyList<WeatherTimeSlice> fallback,
      CancellationToken cancellationToken)
    {
      Uri? endpoint = null;
      try
      {
        if (string.IsNullOrWhiteSpace(provider.Endpoint)) throw new ArgumentException("Endpoint is missing");
        endpoint = BuildUri(provider.Endpoint, query, provider.ApiKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
          request.Headers.TryAddWithoutValidation("Authorization", provider.ApiKey);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
          return BuildSample(provider, query, $"HTTP {(int)response.StatusCode}", "API error, showing sample.", fallback, endpoint);

        await using var payload = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);

        return new WeatherProviderResult
        {
          Provider = provider.Name,
          Source = provider.Source,
          Status = "Live",
          Note = $"Live data from {provider.Source}.",
          RetrievedAt = DateTimeOffset.UtcNow,
          Periods = WeatherShaper.ShapeSlices(document, fallback, label),
          Endpoint = endpoint
        };
      }
      catch (Exception ex)
      {
        return BuildSample(provider, query, "Unavailable", ex.Message, fallback, endpoint);
      }
    }

    private static WeatherProviderResult BuildSample(WeatherProviderOptions provider, WeatherQuery query, string status, string note, IReadOnlyList<WeatherTimeSlice> slices, Uri? endpoint = null) => new()
    {
        Provider = provider.Name ?? "Unknown",
        Source = provider.Source ?? "Unknown",
        Status = status,
        Note = note,
        RetrievedAt = DateTimeOffset.UtcNow,
        Periods = slices,
        Endpoint = endpoint
    };

    private static Uri BuildUri(string template, WeatherQuery query, string? apiKey = null)
    {
      var formatted = template
        .Replace("{lat}", query.Latitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{latitude}", query.Latitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{lon}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{longitude}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{lng}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

      if (!string.IsNullOrWhiteSpace(apiKey))
         formatted = formatted.Replace("{key}", apiKey, StringComparison.OrdinalIgnoreCase).Replace("{apiKey}", apiKey, StringComparison.OrdinalIgnoreCase);

      return new Uri(formatted, UriKind.Absolute);
    }
  }
}
