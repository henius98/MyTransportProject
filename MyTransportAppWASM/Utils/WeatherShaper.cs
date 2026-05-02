using System.Globalization;
using System.Text.Json;
using MyTransportAppWASM.Models.Weather;

namespace MyTransportAppWASM.Utils
{
    public static class WeatherShaper
    {
        public static IReadOnlyList<WeatherTimeSlice> ShapeSlices(JsonDocument document, IReadOnlyList<WeatherTimeSlice> fallback, string label)
        {
            JsonElement root = document.RootElement;
            
            double? temperature = FindFirstDouble(root, "temperature", "temperature_2m", "temperature_2m_max", "temp");
            double? precipitation = FindFirstDouble(root, "precipitation", "rain", "precip_mm", "precip");
            double? probability = FindFirstDouble(root, "probability", "precipitation_probability", "precipitation_probability_max", "pop");
            probability = NormalizeProbability(probability);
            
            string? summary = FindFirstString(root, "summary", "description", "weather", "forecast");
            string? wind = FindFirstString(root, "wind", "wind_speed", "wind_speed_10m");
            DateTimeOffset time = FindFirstDate(root, "time", "datetime", "ob_time", "ts") ?? DateTimeOffset.UtcNow;

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
            if (probability is null) return null;
            double value = probability.Value;
            if (value > 1 && value <= 100) return value / 100d;
            if (value > 0 && value <= 1) return value;
            return 0;
        }

        private static double? FindFirstDouble(JsonElement element, params string[] names)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    double? nested = FindFirstDouble(item, names);
                    if (nested.HasValue) return nested;
                }
            }

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
                    if (nested.HasValue) return nested;
                }
            }
            return null;
        }

        private static string? FindFirstString(JsonElement element, params string[] names)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? nested = FindFirstString(item, names);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
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
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
            return null;
        }

        private static DateTimeOffset? FindFirstDate(JsonElement element, params string[] names)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    DateTimeOffset? nestedItem = FindFirstDate(item, names);
                    if (nestedItem.HasValue) return nestedItem;
                }
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if ((names.Length == 0 || names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase))) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
                    {
                        return parsed;
                    }

                    if ((names.Length == 0 || names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase))) &&
                        property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out long epoch))
                    {
                        try { return DateTimeOffset.FromUnixTimeSeconds(epoch); } catch { }
                    }

                    DateTimeOffset? nested = FindFirstDate(property.Value, names);
                    if (nested.HasValue) return nested;
                }
            }
            return null;
        }
    }
}
