
namespace MyTransportAppWASM.Models
{
  public static class WeatherForecastLimits
  {
    public const int OpenMeteoForecastDays = 16;
  }

  public record WeatherQuery
  {
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceName { get; set; }
    public string? LocationId { get; set; }
    public DateTimeOffset? Departure { get; set; }
    public DateTimeOffset? Return { get; set; }
  }

  public record WeatherTimeSlice
  {
    public string Label { get; init; } = string.Empty;
    public DateTimeOffset Time { get; init; }
    public double? TemperatureC { get; init; }
    public double? MinTemperatureC { get; init; }
    public double? MaxTemperatureC { get; init; }
    public double? PrecipitationMm { get; init; }
    public double? ProbabilityOfRain { get; init; }
    public string? Wind { get; init; }
    public double? UvIndex { get; init; }
    public DateTimeOffset? Sunrise { get; init; }
    public DateTimeOffset? Sunset { get; init; }
    public string? Summary { get; init; }
    public string? Icon { get; init; }
    public bool IsPlaceholder { get; init; } = false;
  }

  public record WeatherProviderResult
  {
    public string Provider { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool IsCached { get; init; }
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<WeatherTimeSlice> Periods { get; init; } = Array.Empty<WeatherTimeSlice>();
    public Uri? Endpoint { get; init; }
  }

  public record WeatherPlanResult
  {
    public required WeatherQuery Query { get; init; }
    public required IReadOnlyList<WeatherProviderResult> Realtime { get; init; }
    public required IReadOnlyList<WeatherProviderResult> Outlooks { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
  }

  public record WeatherProviderOptions
  {
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; }
  }

  public record MetMalaysiaWeatherProviderOptions : WeatherProviderOptions
  {
    public string Endpoints { get; set; } = string.Empty;
  }

  public record KeyedWeatherProviderOptions : WeatherProviderOptions
  {
    public Dictionary<string, string> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
  }

  public class WeatherOptions
  {
    public MetMalaysiaWeatherProviderOptions MetMalaysia { get; set; } = new();
    public KeyedWeatherProviderOptions OpenMeteo { get; set; } = new();
    public KeyedWeatherProviderOptions SingaporeNEA { get; set; } = new();
  }
}
