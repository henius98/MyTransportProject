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

        var result = WeatherShaper.ShapeSlices(document, Array.Empty<WeatherTimeSlice>(), "Realtime");

        Assert.Equal(2, result.Count);
        Assert.Equal(29.5, result[0].TemperatureC);
        Assert.Equal(0.2, result[0].ProbabilityOfRain!.Value, 6);
        Assert.Equal("Mainly clear", result[0].Summary);
        Assert.Equal("Thunderstorm", result[1].Summary);
    }

    [Fact]
    public void ShapeSlices_MapsOpenMeteoDailyTripDetails()
    {
        using var document = JsonDocument.Parse("""
            {
              "utc_offset_seconds": 28800,
              "daily_units": { "wind_speed_10m_max": "m/s" },
              "daily": {
                "time": ["2026-09-24"],
                "temperature_2m_max": [30.1],
                "temperature_2m_min": [24.2],
                "precipitation_probability_max": [93],
                "precipitation_sum": [7.4],
                "wind_speed_10m_max": [8.5],
                "uv_index_max": [9.2],
                "sunrise": ["2026-09-24T07:04"],
                "sunset": ["2026-09-24T19:11"],
                "weather_code": [95]
              }
            }
            """);

        var day = Assert.Single(WeatherShaper.ShapeSlices(document, [], "Outlook"));

        Assert.Equal(30.1, day.MaxTemperatureC);
        Assert.Equal(24.2, day.MinTemperatureC);
        Assert.Equal(0.93, day.ProbabilityOfRain);
        Assert.Equal(7.4, day.PrecipitationMm);
        Assert.Equal("8.5 m/s", day.Wind);
        Assert.Equal(9.2, day.UvIndex);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 7, 4, 0, TimeSpan.FromHours(8)), day.Sunrise);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 19, 11, 0, TimeSpan.FromHours(8)), day.Sunset);
    }

    [Fact]
    public void ShapeSlices_KeepsHourlyRainChancePrecipitationAndWindSeparate()
    {
        using var document = JsonDocument.Parse("""
            {
              "hourly_units": { "wind_speed_10m": "km/h" },
              "hourly": {
                "time": ["2026-09-23T00:00:00+08:00", "2026-09-23T01:00:00+08:00"],
                "precipitation": [4.2, 0],
                "precipitation_probability": [75, 1],
                "wind_speed_10m": [12.5, 0]
              }
            }
            """);

        var result = WeatherShaper.ShapeSlices(document, [], "Outlook");

        Assert.Equal(4.2, result[0].PrecipitationMm);
        Assert.Equal(0.75, result[0].ProbabilityOfRain);
        Assert.Equal("12.5 km/h", result[0].Wind);
        Assert.Equal(0, result[1].PrecipitationMm);
        Assert.Equal(0.01, result[1].ProbabilityOfRain);
        Assert.Equal("0 km/h", result[1].Wind);
    }

    [Fact]
    public void ShapeSlices_DoesNotTreatRainfallAsProbabilityWhenProbabilityIsMissing()
    {
        using var document = JsonDocument.Parse("""
            {
              "hourly": {
                "time": ["2026-09-23T00:00:00+08:00"],
                "precipitation": [8.5]
              }
            }
            """);

        var slice = Assert.Single(WeatherShaper.ShapeSlices(document, [], "Outlook"));

        Assert.Equal(8.5, slice.PrecipitationMm);
        Assert.Null(slice.ProbabilityOfRain);
        Assert.Null(slice.Wind);
    }

    [Fact]
    public void ShapeSlices_HandlesNullAndShortHourlyArraysAndUsesReportedWindUnit()
    {
        using var document = JsonDocument.Parse("""
            {
              "hourly_units": { "wind_speed_10m": "m/s" },
              "hourly": {
                "time": ["2026-09-23T00:00:00+08:00", "2026-09-23T01:00:00+08:00"],
                "precipitation": [null],
                "precipitation_probability": [null],
                "wind_speed_10m": [3.5]
              }
            }
            """);

        var result = WeatherShaper.ShapeSlices(document, [], "Outlook");

        Assert.All(result, slice => Assert.Null(slice.PrecipitationMm));
        Assert.All(result, slice => Assert.Null(slice.ProbabilityOfRain));
        Assert.Equal("3.5 m/s", result[0].Wind);
        Assert.Null(result[1].Wind);
    }

    [Fact]
    public void ShapeSlices_AppliesOpenMeteoUtcOffsetToLocalTimestamps()
    {
        using var document = JsonDocument.Parse("""
            {
              "utc_offset_seconds": 28800,
              "hourly": {
                "time": ["2026-09-23T00:00"],
                "temperature_2m": [27]
              }
            }
            """);

        var result = WeatherShaper.ShapeSlices(document, Array.Empty<WeatherTimeSlice>(), "Realtime");

        var slice = Assert.Single(result);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.FromHours(8)), slice.Time);
    }

    [Fact]
    public void ShapeSlices_MatchesPropertyNamesCaseInsensitively()
    {
        using var document = JsonDocument.Parse("""
            { "TEMPERATURE": 31, "TIME": "2026-08-26T12:00:00Z", "SUMMARY": "Clear" }
            """);

        var result = WeatherShaper.ShapeSlices(document, Array.Empty<WeatherTimeSlice>(), "Realtime");

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

        var result = WeatherShaper.ShapeSlices(document, Array.Empty<WeatherTimeSlice>(), "Realtime");

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

        var result = WeatherShaper.ShapeSlices(document, fallback, "Realtime");

        Assert.Same(fallback, result);
    }
}
