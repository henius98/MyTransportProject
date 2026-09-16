using System.Text.Json;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class WeatherShaperTests
{
    [Fact]
    public void ShapeSlices_MapsNestedDailyPeriodsInOneRecord()
    {
        using var document = JsonDocument.Parse("""
            [
              {
                "location": { "location_name": "Kuala Lumpur" },
                "forecast": {
                  "date": "2026-08-26T00:00:00Z",
                  "temperature_2m_max": 32.5,
                  "temperature_2m_min": 24.0,
                  "precipitation": 4.2,
                  "precipitation_probability": 75,
                  "wind_speed": "12 km/h",
                  "morning_forecast": "Partly cloudy",
                  "afternoon_forecast": "Thunderstorms in a few places",
                  "night_forecast": "Rain"
                }
              }
            ]
            """);

        var result = WeatherShaper.ShapeSlices(document, Array.Empty<WeatherTimeSlice>(), "Outlook");

        Assert.Collection(
            result,
            morning => Assert.Equal("Pagi", morning.Label),
            afternoon => Assert.Equal("Petang", afternoon.Label),
            night => Assert.Equal("Malam", night.Label));
        Assert.All(result, slice => Assert.Equal(0.75, slice.ProbabilityOfRain!.Value, 6));
        Assert.All(result, slice => Assert.Equal(32.5, slice.TemperatureC));
    }

    [Fact]
    public void ShapeSlices_MapsOpenMeteoParallelArrays()
    {
        using var document = JsonDocument.Parse("""
            {
              "hourly": {
                "time": ["2026-08-26T00:00:00Z", "2026-08-26T01:00:00Z"],
                "temperature_2m": [29.5, 30.0],
                "precipitation_probability": [20, 80],
                "weather_code": [1, 95]
              }
            }
            """);

        var result = WeatherShaper.ShapeSlices(document, Array.Empty<WeatherTimeSlice>(), "Baseline");

        Assert.Equal(2, result.Count);
        Assert.Equal(29.5, result[0].TemperatureC);
        Assert.Equal(0.2, result[0].ProbabilityOfRain!.Value, 6);
        Assert.Equal("Mainly clear", result[0].Summary);
        Assert.Equal("Thunderstorm", result[1].Summary);
    }

    [Fact]
    public void ShapeSlices_MatchesPropertyNamesCaseInsensitively()
    {
        using var document = JsonDocument.Parse("""
            { "TEMPERATURE": 31, "TIME": "2026-08-26T12:00:00Z", "SUMMARY": "Clear" }
            """);

        var result = WeatherShaper.ShapeSlices(document, Array.Empty<WeatherTimeSlice>(), "Baseline");

        var slice = Assert.Single(result);
        Assert.Equal(31, slice.TemperatureC);
        Assert.Equal("Clear", slice.Summary);
    }

    [Fact]
    public void ShapeSlices_IgnoresMalformedParallelValueCollections()
    {
        using var document = JsonDocument.Parse("""
            {
              "hourly": {
                "time": ["2026-08-26T00:00:00Z"],
                "temperature_2m": 31,
                "precipitation_probability": "unknown",
                "weather_code": {}
              }
            }
            """);

        var result = WeatherShaper.ShapeSlices(document, Array.Empty<WeatherTimeSlice>(), "Baseline");

        var slice = Assert.Single(result);
        Assert.Null(slice.TemperatureC);
        Assert.Null(slice.ProbabilityOfRain);
        Assert.Null(slice.Summary);
    }

    [Fact]
    public void ShapeSlices_ReturnsProvidedFallback_WhenNoWeatherFieldsExist()
    {
        using var document = JsonDocument.Parse("""{ "metadata": { "provider": "test" } }""");
        IReadOnlyList<WeatherTimeSlice> fallback =
        [
            new WeatherTimeSlice { Label = "N/A", IsPlaceholder = true }
        ];

        var result = WeatherShaper.ShapeSlices(document, fallback, "Baseline");

        Assert.Same(fallback, result);
    }
}
