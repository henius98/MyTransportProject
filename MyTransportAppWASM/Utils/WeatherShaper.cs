using System.Globalization;
using System.Text.Json;
using MyTransportAppWASM.Models.Weather;

namespace MyTransportAppWASM.Utils
{
    public static class WeatherShaper
    {
        /// <summary>
        /// Maximum recursion depth for JSON traversal to prevent stack overflow on adversarial payloads.
        /// </summary>
        private const int MaxTraversalDepth = 10;

        public static IReadOnlyList<WeatherTimeSlice> ShapeSlices(JsonDocument document, IReadOnlyList<WeatherTimeSlice> fallback, string label)
        {
            JsonElement root = document.RootElement;
            
            double? temperature = FindFirstDouble(root, 0, "temperature", "temperature_2m", "temperature_2m_max", "temp");
            double? precipitation = FindFirstDouble(root, 0, "precipitation", "rain", "precip_mm", "precip");
            double? probability = FindFirstDouble(root, 0, "probability", "precipitation_probability", "precipitation_probability_max", "pop");
            probability = NormalizeProbability(probability);
            
            string? summary = FindFirstString(root, 0, "summary", "description", "weather", "forecast");
            string? wind = FindFirstString(root, 0, "wind", "wind_speed", "wind_speed_10m");
            DateTimeOffset time = FindFirstDate(root, 0, "time", "datetime", "ob_time", "ts") ?? DateTimeOffset.UtcNow;

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

        private static double? FindFirstDouble(JsonElement element, int depth, params string[] names)
        {
            if (depth >= MaxTraversalDepth) return null;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    double? nested = FindFirstDouble(item, depth + 1, names);
                    if (nested.HasValue) return nested;
                }
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (MatchesAnyName(property.Name, names))
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

                    double? nested = FindFirstDouble(property.Value, depth + 1, names);
                    if (nested.HasValue) return nested;
                }
            }
            return null;
        }

        private static string? FindFirstString(JsonElement element, int depth, params string[] names)
        {
            if (depth >= MaxTraversalDepth) return null;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? nested = FindFirstString(item, depth + 1, names);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (MatchesAnyName(property.Name, names) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }

                    string? nested = FindFirstString(property.Value, depth + 1, names);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }
            }
            return null;
        }

        private static DateTimeOffset? FindFirstDate(JsonElement element, int depth, params string[] names)
        {
            if (depth >= MaxTraversalDepth) return null;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    DateTimeOffset? nestedItem = FindFirstDate(item, depth + 1, names);
                    if (nestedItem.HasValue) return nestedItem;
                }
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if ((names.Length == 0 || MatchesAnyName(property.Name, names)) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
                    {
                        return parsed;
                    }

                    if ((names.Length == 0 || MatchesAnyName(property.Name, names)) &&
                        property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out long epoch))
                    {
                        try { return DateTimeOffset.FromUnixTimeSeconds(epoch); } catch { }
                    }

                    DateTimeOffset? nested = FindFirstDate(property.Value, depth + 1, names);
                    if (nested.HasValue) return nested;
                }
            }
            return null;
        }

        /// <summary>
        /// Manual loop to match property names — avoids LINQ .Any() delegate allocation on hot path.
        /// </summary>
        private static bool MatchesAnyName(string propertyName, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], propertyName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
