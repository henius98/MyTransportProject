using System.Globalization;
using System.Text.Json;

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
      var results = new List<WeatherTimeSlice>();

      if (root.ValueKind == JsonValueKind.Object)
      {
        JsonElement? parallelNode = null;
        if (root.TryGetProperty("daily", out var daily)) parallelNode = daily;
        else if (root.TryGetProperty("hourly", out var hourly)) parallelNode = hourly;

        if (parallelNode.HasValue && parallelNode.Value.ValueKind == JsonValueKind.Object)
        {
          var node = parallelNode.Value;
          if (node.TryGetProperty("time", out var timeArray) && timeArray.ValueKind == JsonValueKind.Array)
          {
            int count = timeArray.GetArrayLength();
            JsonElement? tempArray = GetPropertyAnyName(node, "temperature_2m_max", "temperature_2m", "temperature");
            JsonElement? precipArray = GetPropertyAnyName(node, "precipitation_probability_max", "precipitation_probability", "precipitation", "rain");
            JsonElement? weatherCodeArray = GetPropertyAnyName(node, "weather_code", "weathercode");

            for (int i = 0; i < count; i++)
            {
              DateTimeOffset time = DateTimeOffset.UtcNow;
              if (timeArray[i].ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(timeArray[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) time = t;
              else if (timeArray[i].ValueKind == JsonValueKind.Number && timeArray[i].TryGetInt64(out long epoch)) time = DateTimeOffset.FromUnixTimeSeconds(epoch);

              double? temp = tempArray.HasValue && tempArray.Value.GetArrayLength() > i && tempArray.Value[i].ValueKind == JsonValueKind.Number ? tempArray.Value[i].GetDouble() : null;
              double? precip = precipArray.HasValue && precipArray.Value.GetArrayLength() > i && precipArray.Value[i].ValueKind == JsonValueKind.Number ? precipArray.Value[i].GetDouble() : null;
              double? weatherCodeDouble = weatherCodeArray.HasValue && weatherCodeArray.Value.GetArrayLength() > i && weatherCodeArray.Value[i].ValueKind == JsonValueKind.Number ? weatherCodeArray.Value[i].GetDouble() : null;

              string? summary = null;
              string? icon = null;
              if (weatherCodeDouble.HasValue)
              {
                var localTime = time.ToOffset(TimeSpan.FromHours(8));
                bool isNight = localTime.Hour < 7 || localTime.Hour > 19;
                (summary, icon) = MapWmoCode(weatherCodeDouble.Value, isNight);
              }

              results.Add(new WeatherTimeSlice
              {
                Label = label,
                Time = time,
                TemperatureC = temp,
                ProbabilityOfRain = NormalizeProbability(precip),
                Summary = summary,
                Icon = icon
              });
            }
            if (results.Count > 0) return results;
          }
        }
      }

      if (root.ValueKind == JsonValueKind.Array)
      {
        foreach (var item in root.EnumerateArray())
        {
          if (item.ValueKind == JsonValueKind.Object)
          {
            var sliceList = ExtractSingleSlice(item, null, label);
            if (sliceList != null && sliceList.Count > 0 && !sliceList[0].IsPlaceholder)
            {
              results.AddRange(sliceList);
            }
          }
        }
        if (results.Count > 0) return results;
      }

      return ExtractSingleSlice(root, fallback, label);
    }

    private static JsonElement? GetPropertyAnyName(JsonElement element, params string[] names)
    {
      foreach (var prop in element.EnumerateObject())
      {
        if (MatchesAnyName(prop.Name, names)) return prop.Value;
      }
      return null;
    }

    private static IReadOnlyList<WeatherTimeSlice> ExtractSingleSlice(JsonElement root, IReadOnlyList<WeatherTimeSlice>? fallback, string label)
    {
      double? temperature = FindFirstDouble(root, 0, "temperature", "temperature_2m", "temp");
      double? maxTemp = FindFirstDouble(root, 0, "temperature_2m_max", "max_temp");
      double? minTemp = FindFirstDouble(root, 0, "temperature_2m_min", "min_temp");
      temperature ??= maxTemp;
      double? precipitation = FindFirstDouble(root, 0, "precipitation", "rain", "precip_mm", "precip");
      double? probability = FindFirstDouble(root, 0, "probability", "precipitation_probability", "precipitation_probability_max", "pop");
      probability = NormalizeProbability(probability);

      string? wind = FindFirstString(root, 0, "wind", "wind_speed", "wind_speed_10m");
      DateTimeOffset time = FindFirstDate(root, 0, "time", "datetime", "ob_time", "ts", "date") ?? DateTimeOffset.UtcNow;
      
      string? morning = FindFirstString(root, 0, "morning_forecast");
      string? afternoon = FindFirstString(root, 0, "afternoon_forecast");
      string? night = FindFirstString(root, 0, "night_forecast");

      if (morning != null || afternoon != null || night != null)
      {
          var list = new List<WeatherTimeSlice>();
          if (morning != null)
          {
              list.Add(new WeatherTimeSlice
              {
                  Label = "Pagi",
                  Time = time,
                  TemperatureC = temperature,
                  MinTemperatureC = minTemp,
                  MaxTemperatureC = maxTemp,
                  PrecipitationMm = precipitation,
                  ProbabilityOfRain = probability,
                  Wind = wind,
                  Summary = morning,
                  Icon = GetIconForSummaryString(morning, time)
              });
          }
          if (afternoon != null)
          {
              var petangTime = time.AddHours(6);
              list.Add(new WeatherTimeSlice
              {
                  Label = "Petang",
                  Time = petangTime,
                  TemperatureC = temperature,
                  MinTemperatureC = minTemp,
                  MaxTemperatureC = maxTemp,
                  PrecipitationMm = precipitation,
                  ProbabilityOfRain = probability,
                  Wind = wind,
                  Summary = afternoon,
                  Icon = GetIconForSummaryString(afternoon, petangTime)
              });
          }
          if (night != null)
          {
              var malamTime = time.AddHours(12);
              list.Add(new WeatherTimeSlice
              {
                  Label = "Malam",
                  Time = malamTime,
                  TemperatureC = temperature,
                  MinTemperatureC = minTemp,
                  MaxTemperatureC = maxTemp,
                  PrecipitationMm = precipitation,
                  ProbabilityOfRain = probability,
                  Wind = wind,
                  Summary = night,
                  Icon = GetIconForSummaryString(night, malamTime)
              });
          }
          return list;
      }

      string? summary = FindFirstString(root, 0, "summary", "description", "weather", "forecast");
      
      string? icon = null;
      if (summary == null)
      {
        double? weatherCode = FindFirstDouble(root, 0, "weather_code", "weathercode");
        if (weatherCode.HasValue)
        {
          var localTime = time.ToOffset(TimeSpan.FromHours(8));
          bool isNight = localTime.Hour < 7 || localTime.Hour > 19;
          (summary, icon) = MapWmoCode(weatherCode.Value, isNight);
        }
      }
      else
      {
         icon = GetIconForSummaryString(summary, time);
      }



      if (temperature is null && precipitation is null && probability is null && summary is null && wind is null)
      {
        return fallback ?? new List<WeatherTimeSlice>();
      }

      return new List<WeatherTimeSlice>
      {
          new()
          {
              Label = label,
              Time = time,
              TemperatureC = temperature,
              MinTemperatureC = minTemp,
              MaxTemperatureC = maxTemp,
              PrecipitationMm = precipitation,
              ProbabilityOfRain = probability,
              Wind = wind,
              Summary = summary,
              Icon = icon
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

    private static (string Summary, string Icon) MapWmoCode(double code, bool isNight = false)
    {
      int c = (int)code;
      return c switch
      {
        0 => ("Clear sky", isNight ? "bi-moon-stars-fill text-primary" : "bi-sun-fill text-warning"),
        1 => ("Mainly clear", isNight ? "bi-moon-stars-fill text-primary" : "bi-sun-fill text-warning"),
        2 => ("Partly cloudy", isNight ? "bi-cloud-moon-fill text-secondary" : "bi-cloud-sun-fill text-primary"),
        3 => ("Overcast", "bi-clouds-fill text-secondary"),
        45 => ("Fog", "bi-cloud-fog2-fill text-secondary"),
        48 => ("Depositing rime fog", "bi-cloud-fog2-fill text-secondary"),
        51 => ("Light drizzle", "bi-cloud-drizzle text-info"),
        53 => ("Moderate drizzle", "bi-cloud-drizzle-fill text-info"),
        55 => ("Dense drizzle", "bi-cloud-drizzle-fill text-info"),
        56 => ("Light freezing drizzle", "bi-cloud-sleet-fill text-info"),
        57 => ("Dense freezing drizzle", "bi-cloud-sleet-fill text-info"),
        61 => ("Slight rain", "bi-cloud-drizzle-fill text-info"),
        63 => ("Moderate rain", "bi-cloud-rain-fill text-primary"),
        65 => ("Heavy rain", "bi-cloud-rain-heavy-fill text-primary"),
        66 => ("Light freezing rain", "bi-cloud-sleet-fill text-info"),
        67 => ("Heavy freezing rain", "bi-cloud-sleet-fill text-info"),
        71 => ("Slight snow fall", "bi-snow text-primary"),
        73 => ("Moderate snow fall", "bi-snow text-primary"),
        75 => ("Heavy snow fall", "bi-cloud-snow-fill text-primary"),
        77 => ("Snow grains", "bi-snow text-primary"),
        80 => ("Slight rain showers", "bi-cloud-drizzle-fill text-info"),
        81 => ("Moderate rain showers", "bi-cloud-rain-fill text-primary"),
        82 => ("Violent rain showers", "bi-cloud-rain-heavy-fill text-primary"),
        85 => ("Slight snow showers", "bi-snow text-primary"),
        86 => ("Heavy snow showers", "bi-cloud-snow-fill text-primary"),
        95 => ("Thunderstorm", "bi-cloud-lightning-rain-fill text-warning"),
        96 => ("Thunderstorm with slight hail", "bi-cloud-hail text-warning"),
        99 => ("Thunderstorm with heavy hail", "bi-cloud-hail text-warning"),
        _ => ("N/A", "bi-cloud text-secondary")
      };
    }

    private static string GetIconForSummaryString(string? summary, DateTimeOffset time)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "bi-cloud text-secondary";
        
        var lower = summary.ToLowerInvariant();
        var localTime = time.ToOffset(TimeSpan.FromHours(8));
        bool isNight = localTime.Hour < 7 || localTime.Hour > 19;

        if (lower.Contains("ribut petir") || lower.Contains("thunder") || lower.Contains("lightning") || lower.Contains("squall"))
        {
            if (lower.Contains("hail") || lower.Contains("batu")) return "bi-cloud-hail text-warning";
            if (lower.Contains("beberapa tempat") || lower.Contains("satu dua tempat") || lower.Contains("few places") || lower.Contains("isolated")) return "bi-cloud-lightning-fill text-warning";
            return "bi-cloud-lightning-rain-fill text-warning";
        }
        if (lower.Contains("snow") || lower.Contains("salji"))
        {
            if (lower.Contains("heavy") || lower.Contains("lebat")) return "bi-cloud-snow-fill text-primary";
            return "bi-snow text-primary";
        }
        if (lower.Contains("freezing") || lower.Contains("beku")) return "bi-cloud-sleet-fill text-info";
        if (lower.Contains("fog") || lower.Contains("kabus") || lower.Contains("haze") || lower.Contains("rime")) return "bi-cloud-fog2-fill text-secondary";
        if (lower.Contains("heavy rain") || lower.Contains("violent") || lower.Contains("hujan lebat")) return "bi-cloud-rain-heavy-fill text-primary";
        if (lower.Contains("moderate rain") || lower.Contains("hujan sederhana")) return "bi-cloud-rain-fill text-primary";
        if (lower.Contains("slight rain") || lower.Contains("hujan renyai") || lower.Contains("beberapa tempat")) return "bi-cloud-drizzle-fill text-info";
        if (lower.Contains("drizzle") || lower.Contains("gerimis"))
        {
            if (lower.Contains("light") || lower.Contains("ringan")) return "bi-cloud-drizzle text-info";
            return "bi-cloud-drizzle-fill text-info";
        }
        if (lower.Contains("hujan") || lower.Contains("rain") || lower.Contains("shower")) return "bi-cloud-rain-fill text-info";
        if (lower.Contains("overcast") || lower.Contains("mendung")) return "bi-clouds-fill text-secondary";
        if (lower.Contains("partly cloudy") || lower.Contains("separa mendung") || lower.Contains("cloudy")) return isNight ? "bi-cloud-moon-fill text-secondary" : "bi-cloud-sun-fill text-primary";
        if (lower.Contains("clear") || lower.Contains("tiada hujan") || lower.Contains("fair") || lower.Contains("sunny") || lower.Contains("cerah")) return isNight ? "bi-moon-stars-fill text-primary" : "bi-sun-fill text-warning";
        return isNight ? "bi-cloud-moon-fill text-secondary" : "bi-cloud-sun-fill text-primary";
    }
  }
}
