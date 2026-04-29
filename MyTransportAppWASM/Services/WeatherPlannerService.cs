using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Models.Weather;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
  public class WeatherPlannerService : IWeatherPlannerService
  {
    private const string UserAgent = "MyTransportApp/1.0 (weather planner)";
    
    // Default fallback options if appsettings is empty
    private static readonly WeatherProviderOptions DefaultMetMalaysia = new()
    {
      Name = "MetMalaysia (Sample)",
      Source = "myCuaca + MET API",
      Endpoint = "https://api.met.gov.my/v2/weather/forecast?lat={lat}&lon={lon}",
      Enabled = false
    };

    private static readonly WeatherProviderOptions DefaultRadar = new()
    {
      Name = "MetMalaysia Radar (Sample)",
      Source = "Next-hour rain radar",
      Endpoint = "https://api.met.gov.my/v2/weather/radar/nowcast?lat={lat}&lon={lon}",
      Enabled = false
    };

    private static readonly IReadOnlyList<WeatherProviderOptions> DefaultOutlooks = new List<WeatherProviderOptions>
    {
      new()
      {
        Name = "Open-Meteo",
        Source = "open-meteo.com",
        Endpoint = "https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true&daily=temperature_2m_max,precipitation_probability_max&timezone=auto",
        Enabled = true
      }
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public WeatherPlannerService(HttpClient httpClient, IConfiguration configuration)
    {
      _httpClient = httpClient;
      _configuration = configuration;
    }

    public async Task<WeatherPlanResult> GetWeatherPlanAsync(WeatherQuery query, CancellationToken cancellationToken = default)
    {
      try
      {
        Task<WeatherProviderResult> baselineTask = FetchMetMalaysiaAsync(query, cancellationToken);
        Task<WeatherProviderResult> radarTask = FetchRadarAsync(query, cancellationToken);
        Task<IReadOnlyList<WeatherProviderResult>> outlooksTask = FetchOutlooksAsync(query, cancellationToken);

        await Task.WhenAll(baselineTask, radarTask, outlooksTask);

        List<string> notes = new()
        {
          "CORS-enabled providers like Open-Meteo are active for live data.",
          $"Request: {query.PlaceName ?? "Custom location"} ({query.Latitude:F3}, {query.Longitude:F3})."
        };

        return new WeatherPlanResult
        {
          Query = query,
          Baseline = await baselineTask,
          Radar = await radarTask,
          Outlooks = await outlooksTask,
          Notes = notes
        };
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Global weather planner failure: {ex.Message}");
        return new WeatherPlanResult
        {
          Query = query,
          Baseline = BuildSample(DefaultMetMalaysia, query, "Error", "Internal error occurred.", SampleBaseline(query)),
          Radar = BuildSample(DefaultRadar, query, "Error", "Internal error occurred.", SampleRadar(query)),
          Outlooks = new List<WeatherProviderResult>(),
          Notes = new List<string> { $"Error: {ex.Message}" }
        };
      }
    }

    private async Task<WeatherProviderResult> FetchMetMalaysiaAsync(WeatherQuery query, CancellationToken cancellationToken)
    {
      try
      {
        WeatherProviderOptions provider = ResolveProvider("WeatherProviders:MetMalaysia", DefaultMetMalaysia);
        IReadOnlyList<WeatherTimeSlice> fallback = SampleBaseline(query);

        if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.Endpoint))
        {
          return BuildSample(provider, query, "Not configured", "Set WeatherProviders:MetMalaysia in appsettings to call the official baseline.", fallback);
        }

        return await FetchAndShapeAsync(provider, query, "Baseline", fallback, cancellationToken);
      }
      catch (Exception ex)
      {
        return BuildSample(DefaultMetMalaysia, query, "Error", $"Failed to resolve provider: {ex.Message}", SampleBaseline(query));
      }
    }

    private async Task<WeatherProviderResult> FetchRadarAsync(WeatherQuery query, CancellationToken cancellationToken)
    {
      try
      {
        WeatherProviderOptions provider = ResolveProvider("WeatherProviders:Radar", DefaultRadar);
        IReadOnlyList<WeatherTimeSlice> fallback = SampleRadar(query);

        if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.Endpoint))
        {
          return BuildSample(provider, query, "Not configured", "Enable WeatherProviders:Radar to see the live next-hour rain radar.", fallback);
        }

        return await FetchAndShapeAsync(provider, query, "Radar", fallback, cancellationToken);
      }
      catch (Exception ex)
      {
        return BuildSample(DefaultRadar, query, "Error", $"Failed to resolve provider: {ex.Message}", SampleRadar(query));
      }
    }

    private async Task<IReadOnlyList<WeatherProviderResult>> FetchOutlooksAsync(WeatherQuery query, CancellationToken cancellationToken)
    {
      try
      {
        IEnumerable<Task<WeatherProviderResult>> tasks = ResolveOutlookProviders()
          .Select(provider =>
          {
            IReadOnlyList<WeatherTimeSlice> fallback = SampleOutlook(query);
            if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.Endpoint))
            {
              return Task.FromResult(BuildSample(provider, query, "Not configured", "Enable this outlook provider to fetch a 1–10 day view.", fallback));
            }

            return FetchAndShapeAsync(provider, query, "Outlook", fallback, cancellationToken);
          });

        WeatherProviderResult[] results = await Task.WhenAll(tasks);
        return results;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Outlooks fetch failed: {ex.Message}");
        return new List<WeatherProviderResult>();
      }
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
        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            throw new ArgumentException("Endpoint is missing");
        }

        endpoint = BuildUri(provider.Endpoint, query);

        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
          request.Headers.TryAddWithoutValidation("Authorization", provider.ApiKey);
        }

        HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
          return BuildSample(provider, query, $"HTTP {(int)response.StatusCode}", $"Endpoint returned {(int)response.StatusCode}. Showing sample data.", fallback, endpoint);
        }

        await using Stream payload = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);

        IReadOnlyList<WeatherTimeSlice> shaped = ShapeSlices(document, fallback, label);
        return new WeatherProviderResult
        {
          Provider = provider.Name,
          Source = provider.Source,
          Status = "Live",
          Note = $"Live data from {provider.Source}.",
          RetrievedAt = DateTimeOffset.UtcNow,
          Periods = shaped,
          Endpoint = endpoint
        };
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"{provider.Name} fetch failed: {ex.Message}");
        return BuildSample(provider, query, "Unavailable", $"API unreachable ({ex.GetType().Name}). This usually means CORS is blocked for this provider in the browser.", fallback, endpoint);
      }
    }

    private WeatherProviderOptions ResolveProvider(string sectionName, WeatherProviderOptions defaults)
    {
      IConfigurationSection section = _configuration.GetSection(sectionName);

      return new WeatherProviderOptions
      {
        Name = section["Name"] ?? defaults.Name,
        Source = section["Source"] ?? defaults.Source,
        Endpoint = section["Endpoint"] ?? defaults.Endpoint,
        ApiKey = section["ApiKey"] ?? defaults.ApiKey,
        Enabled = TryReadBool(section["Enabled"], defaults.Enabled)
      };
    }

    private IReadOnlyList<WeatherProviderOptions> ResolveOutlookProviders()
    {
      IConfigurationSection section = _configuration.GetSection("WeatherProviders:Outlooks");
      List<WeatherProviderOptions> configured = new();

      foreach (IConfigurationSection child in section.GetChildren())
      {
        configured.Add(new WeatherProviderOptions
        {
          Name = child["Name"] ?? child.Key,
          Source = child["Source"] ?? child.Key,
          Endpoint = child["Endpoint"],
          ApiKey = child["ApiKey"],
          Enabled = TryReadBool(child["Enabled"], false)
        });
      }

      return configured.Count > 0 ? configured : DefaultOutlooks.ToList();
    }

    private static WeatherProviderResult BuildSample(
      WeatherProviderOptions provider,
      WeatherQuery query,
      string status,
      string note,
      IReadOnlyList<WeatherTimeSlice> slices,
      Uri? endpoint = null)
    {
      return new WeatherProviderResult
      {
        Provider = provider.Name ?? "Unknown Provider",
        Source = provider.Source ?? "Unknown Source",
        Status = status ?? "Unknown Status",
        Note = note,
        RetrievedAt = DateTimeOffset.UtcNow,
        Periods = slices ?? new List<WeatherTimeSlice>(),
        Endpoint = endpoint
      };
    }

    private static IReadOnlyList<WeatherTimeSlice> SampleBaseline(WeatherQuery query)
    {
      DateTimeOffset now = DateTimeOffset.UtcNow;
      return new List<WeatherTimeSlice>
      {
        new()
        {
          Label = "Daytime",
          Time = now,
          TemperatureC = 30,
          PrecipitationMm = 1.2,
          ProbabilityOfRain = 0.35,
          Summary = "Scattered showers."
        }
      };
    }

    private static IReadOnlyList<WeatherTimeSlice> SampleRadar(WeatherQuery query)
    {
      DateTimeOffset now = DateTimeOffset.UtcNow;
      return new List<WeatherTimeSlice>
      {
        new()
        {
          Label = "Next 60 minutes",
          Time = now,
          PrecipitationMm = 0,
          ProbabilityOfRain = 0.1,
          Summary = "Clear radar scan."
        }
      };
    }

    private static IReadOnlyList<WeatherTimeSlice> SampleOutlook(WeatherQuery query)
    {
      DateTimeOffset today = DateTimeOffset.UtcNow;
      return new List<WeatherTimeSlice>
      {
        new()
        {
          Label = "Sample Outlook",
          Time = today,
          TemperatureC = 31,
          ProbabilityOfRain = 0.4,
          Summary = "Isolated storms possible."
        }
      };
    }

    private static IReadOnlyList<WeatherTimeSlice> ShapeSlices(JsonDocument document, IReadOnlyList<WeatherTimeSlice> fallback, string label)
    {
      JsonElement root = document.RootElement;
      
      // Look for standard field names and Open-Meteo specific ones
      double? temperature = FindFirstDouble(root, "temperature", "temperature_2m", "temperature_2m_max", "temp");
      double? precipitation = FindFirstDouble(root, "precipitation", "rain", "precip_mm");
      double? probability = FindFirstDouble(root, "probability", "precipitation_probability", "precipitation_probability_max", "pop");
      probability = NormalizeProbability(probability);
      
      string? summary = FindFirstString(root, "summary", "description", "weather");
      string? wind = FindFirstString(root, "wind", "wind_speed", "wind_speed_10m");
      DateTimeOffset time = FindFirstDate(root) ?? DateTimeOffset.UtcNow;

      if (temperature is null && precipitation is null && probability is null && summary is null && wind is null)
      {
        return fallback;
      }

      return new List<WeatherTimeSlice>
      {
        new()
        {
          Label = string.IsNullOrWhiteSpace(summary) ? label : summary!,
          Time = time,
          TemperatureC = temperature,
          PrecipitationMm = precipitation,
          ProbabilityOfRain = probability,
          Wind = wind,
          Summary = summary ?? label
        }
      };
    }

    private static double? NormalizeProbability(double? probability)
    {
      if (probability is null)
      {
        return null;
      }

      double value = probability.Value;
      if (value > 1 && value <= 100)
      {
        return value / 100d;
      }

      if (value > 0 && value <= 1)
      {
        return value;
      }

      return 0;
    }

    private static double? FindFirstDouble(JsonElement element, params string[] names)
    {
      if (element.ValueKind == JsonValueKind.Object)
      {
        foreach (JsonProperty property in element.EnumerateObject())
        {
          if (names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase)))
          {
             if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double number))
                return number;
             
             if (property.Value.ValueKind == JsonValueKind.Array)
             {
                foreach (JsonElement item in property.Value.EnumerateArray())
                   if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double n))
                      return n;
             }
          }

          double? nested = FindFirstDouble(property.Value, names);
          if (nested.HasValue)
          {
            return nested;
          }
        }
      }

      return null;
    }

    private static string? FindFirstString(JsonElement element, params string[] names)
    {
      if (element.ValueKind == JsonValueKind.String)
      {
        return element.GetString();
      }

      if (element.ValueKind == JsonValueKind.Object)
      {
        foreach (JsonProperty property in element.EnumerateObject())
        {
          if (names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase)) &&
              property.Value.ValueKind == JsonValueKind.String)
          {
            return property.Value.GetString();
          }

          string? nested = FindFirstString(property.Value, names);
          if (!string.IsNullOrWhiteSpace(nested))
          {
            return nested;
          }
        }
      }

      return null;
    }

    private static DateTimeOffset? FindFirstDate(JsonElement element)
    {
      if (element.ValueKind == JsonValueKind.String &&
          DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
      {
        return parsed;
      }

      if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long epoch))
      {
        try
        {
          return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        catch (ArgumentOutOfRangeException) { }
      }

      if (element.ValueKind == JsonValueKind.Object)
      {
        foreach (JsonProperty property in element.EnumerateObject())
        {
          if (property.Name.Equals("time", StringComparison.OrdinalIgnoreCase))
          {
             if (property.Value.ValueKind == JsonValueKind.Array)
             {
                foreach (JsonElement item in property.Value.EnumerateArray())
                {
                   DateTimeOffset? nestedItem = FindFirstDate(item);
                   if (nestedItem.HasValue) return nestedItem;
                }
             }
          }
          
          DateTimeOffset? nested = FindFirstDate(property.Value);
          if (nested.HasValue) return nested;
        }
      }

      return null;
    }

    private static bool TryReadBool(string? value, bool fallback)
    {
      if (bool.TryParse(value, out bool parsed)) return parsed;
      return fallback;
    }

    private static Uri BuildUri(string template, WeatherQuery query)
    {
      string formatted = template
        .Replace("{lat}", query.Latitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{latitude}", query.Latitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{lon}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{longitude}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
        .Replace("{lng}", query.Longitude.ToString("F4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

      return new Uri(formatted, UriKind.Absolute);
    }
  }
}
