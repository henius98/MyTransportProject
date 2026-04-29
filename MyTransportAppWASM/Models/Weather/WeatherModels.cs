using System;
using System.Collections.Generic;

namespace MyTransportAppWASM.Models.Weather
{
  public record WeatherQuery
  {
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceName { get; set; }
    public DateTimeOffset? Departure { get; set; }
    public DateTimeOffset? Return { get; set; }
  }

  public record WeatherTimeSlice
  {
    public string Label { get; init; } = string.Empty;
    public DateTimeOffset Time { get; init; }
    public double? TemperatureC { get; init; }
    public double? PrecipitationMm { get; init; }
    public double? ProbabilityOfRain { get; init; }
    public string? Wind { get; init; }
    public string? Summary { get; init; }
  }

  public record WeatherProviderResult
  {
    public string Provider { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Note { get; init; }
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<WeatherTimeSlice> Periods { get; init; } = Array.Empty<WeatherTimeSlice>();
    public Uri? Endpoint { get; init; }
  }

  public record WeatherPlanResult
  {
    public required WeatherQuery Query { get; init; }
    public required WeatherProviderResult Baseline { get; init; }
    public required WeatherProviderResult Radar { get; init; }
    public required IReadOnlyList<WeatherProviderResult> Outlooks { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
  }

  public class WeatherProviderOptions
  {
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; }
  }
}
