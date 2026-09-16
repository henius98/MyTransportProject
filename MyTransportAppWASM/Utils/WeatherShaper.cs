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

    private static readonly string[] ParallelTemperatureNames = ["temperature_2m_max", "temperature_2m", "temperature"];
    private static readonly string[] ParallelPrecipitationNames = ["precipitation_probability_max", "precipitation_probability", "precipitation", "rain"];
    private static readonly string[] WeatherCodeNames = ["weather_code", "weathercode"];
    private static readonly string[] TemperatureNames = ["temperature", "temperature_2m", "temp"];
    private static readonly string[] MaximumTemperatureNames = ["temperature_2m_max", "max_temp"];
    private static readonly string[] MinimumTemperatureNames = ["temperature_2m_min", "min_temp"];
    private static readonly string[] PrecipitationNames = ["precipitation", "rain", "precip_mm", "precip"];
    private static readonly string[] ProbabilityNames = ["probability", "precipitation_probability", "precipitation_probability_max", "pop"];
    private static readonly string[] WindNames = ["wind", "wind_speed", "wind_speed_10m"];
    private static readonly string[] TimeNames = ["time", "datetime", "ob_time", "ts", "date"];
    private static readonly string[] MorningForecastNames = ["morning_forecast"];
    private static readonly string[] AfternoonForecastNames = ["afternoon_forecast"];
    private static readonly string[] NightForecastNames = ["night_forecast"];
    private static readonly string[] SummaryNames = ["summary", "description", "weather", "forecast"];

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
            JsonElement? tempArray = GetPropertyAnyName(node, ParallelTemperatureNames);
            JsonElement? precipArray = GetPropertyAnyName(node, ParallelPrecipitationNames);
            JsonElement? weatherCodeArray = GetPropertyAnyName(node, WeatherCodeNames);
            int tempCount = GetArrayLength(tempArray);
            int precipCount = GetArrayLength(precipArray);
            int weatherCodeCount = GetArrayLength(weatherCodeArray);
            var fallbackTime = DateTimeOffset.UtcNow;

            results.EnsureCapacity(count);

            for (int i = 0; i < count; i++)
            {
              DateTimeOffset time = fallbackTime;
              if (TryGetDate(timeArray[i], out var parsedTime)) time = parsedTime;

              double? temp = tempArray.HasValue && tempCount > i && tempArray.Value[i].ValueKind == JsonValueKind.Number ? tempArray.Value[i].GetDouble() : null;
              double? precip = precipArray.HasValue && precipCount > i && precipArray.Value[i].ValueKind == JsonValueKind.Number ? precipArray.Value[i].GetDouble() : null;
              double? weatherCodeDouble = weatherCodeArray.HasValue && weatherCodeCount > i && weatherCodeArray.Value[i].ValueKind == JsonValueKind.Number ? weatherCodeArray.Value[i].GetDouble() : null;

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
        // MetMalaysia records commonly expand into morning, afternoon, and night
        // slices. Size for that shape so large forecasts do not grow and copy the
        // backing array after two-thirds of the output has already been produced.
        results.EnsureCapacity(Math.Min(root.GetArrayLength(), 512) * 3);
        foreach (var item in root.EnumerateArray())
        {
          if (item.ValueKind == JsonValueKind.Object)
          {
            AppendSingleSlices(item, results, label);
          }
        }
        if (results.Count > 0) return results;
      }

      return ExtractSingleSlice(root, fallback, label);
    }

    private static int GetArrayLength(JsonElement? element) =>
      element.HasValue && element.Value.ValueKind == JsonValueKind.Array
        ? element.Value.GetArrayLength()
        : 0;

    private static JsonElement? GetPropertyAnyName(JsonElement element, string[] names)
    {
      foreach (var prop in element.EnumerateObject())
      {
        if (MatchesAnyName(prop.Name, names)) return prop.Value;
      }
      return null;
    }

    private static IReadOnlyList<WeatherTimeSlice> ExtractSingleSlice(JsonElement root, IReadOnlyList<WeatherTimeSlice>? fallback, string label)
    {
      var slices = new List<WeatherTimeSlice>(3);
      return AppendSingleSlices(root, slices, label)
        ? slices
        : fallback ?? Array.Empty<WeatherTimeSlice>();
    }

    private static bool AppendSingleSlices(JsonElement root, List<WeatherTimeSlice> destination, string label)
    {
      var fields = new WeatherFields();
      CollectFields(root, 0, ref fields);

      double? temperature = fields.Temperature ?? fields.MaximumTemperature;
      double? probability = NormalizeProbability(fields.Probability);
      DateTimeOffset time = fields.Time ?? DateTimeOffset.UtcNow;

      if (fields.MorningForecast != null || fields.AfternoonForecast != null || fields.NightForecast != null)
      {
          if (fields.MorningForecast != null)
          {
              destination.Add(new WeatherTimeSlice
              {
                  Label = "Pagi",
                  Time = time,
                  TemperatureC = temperature,
                  MinTemperatureC = fields.MinimumTemperature,
                  MaxTemperatureC = fields.MaximumTemperature,
                  PrecipitationMm = fields.Precipitation,
                  ProbabilityOfRain = probability,
                  Wind = fields.Wind,
                  Summary = fields.MorningForecast,
                  Icon = GetIconForSummaryString(fields.MorningForecast, time)
              });
          }
          if (fields.AfternoonForecast != null)
          {
              var petangTime = time.AddHours(6);
              destination.Add(new WeatherTimeSlice
              {
                  Label = "Petang",
                  Time = petangTime,
                  TemperatureC = temperature,
                  MinTemperatureC = fields.MinimumTemperature,
                  MaxTemperatureC = fields.MaximumTemperature,
                  PrecipitationMm = fields.Precipitation,
                  ProbabilityOfRain = probability,
                  Wind = fields.Wind,
                  Summary = fields.AfternoonForecast,
                  Icon = GetIconForSummaryString(fields.AfternoonForecast, petangTime)
              });
          }
          if (fields.NightForecast != null)
          {
              var malamTime = time.AddHours(12);
              destination.Add(new WeatherTimeSlice
              {
                  Label = "Malam",
                  Time = malamTime,
                  TemperatureC = temperature,
                  MinTemperatureC = fields.MinimumTemperature,
                  MaxTemperatureC = fields.MaximumTemperature,
                  PrecipitationMm = fields.Precipitation,
                  ProbabilityOfRain = probability,
                  Wind = fields.Wind,
                  Summary = fields.NightForecast,
                  Icon = GetIconForSummaryString(fields.NightForecast, malamTime)
              });
          }
          return true;
      }

      string? summary = fields.Summary;
      
      string? icon = null;
      if (summary == null)
      {
        if (fields.WeatherCode.HasValue)
        {
          var localTime = time.ToOffset(TimeSpan.FromHours(8));
          bool isNight = localTime.Hour < 7 || localTime.Hour > 19;
          (summary, icon) = MapWmoCode(fields.WeatherCode.Value, isNight);
        }
      }
      else
      {
         icon = GetIconForSummaryString(summary, time);
      }



      if (temperature is null && fields.Precipitation is null && probability is null && summary is null && fields.Wind is null)
      {
        return false;
      }

      destination.Add(new WeatherTimeSlice
      {
          Label = label,
          Time = time,
          TemperatureC = temperature,
          MinTemperatureC = fields.MinimumTemperature,
          MaxTemperatureC = fields.MaximumTemperature,
          PrecipitationMm = fields.Precipitation,
          ProbabilityOfRain = probability,
          Wind = fields.Wind,
          Summary = summary,
          Icon = icon
      });
      return true;
    }

    private static double? NormalizeProbability(double? probability)
    {
      if (probability is null) return null;
      double value = probability.Value;
      if (value > 1 && value <= 100) return value / 100d;
      if (value > 0 && value <= 1) return value;
      return 0;
    }

    private static void CollectFields(JsonElement element, int depth, ref WeatherFields fields)
    {
      if (depth >= MaxTraversalDepth)
      {
        return;
      }

      if (element.ValueKind == JsonValueKind.Array)
      {
        foreach (JsonElement item in element.EnumerateArray())
        {
          CollectFields(item, depth + 1, ref fields);
        }

        return;
      }

      if (element.ValueKind != JsonValueKind.Object)
      {
        return;
      }

      foreach (JsonProperty property in element.EnumerateObject())
      {
        string propertyName = property.Name;
        JsonElement value = property.Value;

        if (fields.Temperature is null && MatchesAnyName(propertyName, TemperatureNames) && TryGetFirstDouble(value, out var temperature))
          fields.Temperature = temperature;
        if (fields.MaximumTemperature is null && MatchesAnyName(propertyName, MaximumTemperatureNames) && TryGetFirstDouble(value, out var maximumTemperature))
          fields.MaximumTemperature = maximumTemperature;
        if (fields.MinimumTemperature is null && MatchesAnyName(propertyName, MinimumTemperatureNames) && TryGetFirstDouble(value, out var minimumTemperature))
          fields.MinimumTemperature = minimumTemperature;
        if (fields.Precipitation is null && MatchesAnyName(propertyName, PrecipitationNames) && TryGetFirstDouble(value, out var precipitation))
          fields.Precipitation = precipitation;
        if (fields.Probability is null && MatchesAnyName(propertyName, ProbabilityNames) && TryGetFirstDouble(value, out var probability))
          fields.Probability = probability;
        if (fields.WeatherCode is null && MatchesAnyName(propertyName, WeatherCodeNames) && TryGetFirstDouble(value, out var weatherCode))
          fields.WeatherCode = weatherCode;

        if (fields.Wind is null && MatchesAnyName(propertyName, WindNames) && value.ValueKind == JsonValueKind.String)
          fields.Wind = value.GetString();
        if (fields.MorningForecast is null && MatchesAnyName(propertyName, MorningForecastNames) && value.ValueKind == JsonValueKind.String)
          fields.MorningForecast = value.GetString();
        if (fields.AfternoonForecast is null && MatchesAnyName(propertyName, AfternoonForecastNames) && value.ValueKind == JsonValueKind.String)
          fields.AfternoonForecast = value.GetString();
        if (fields.NightForecast is null && MatchesAnyName(propertyName, NightForecastNames) && value.ValueKind == JsonValueKind.String)
          fields.NightForecast = value.GetString();
        if (fields.Summary is null && MatchesAnyName(propertyName, SummaryNames) && value.ValueKind == JsonValueKind.String)
          fields.Summary = value.GetString();

        if (fields.Time is null && MatchesAnyName(propertyName, TimeNames) && TryGetDate(value, out var time))
          fields.Time = time;

        CollectFields(value, depth + 1, ref fields);
      }
    }

    private static bool TryGetFirstDouble(JsonElement element, out double value)
    {
      if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
      {
        return true;
      }

      if (element.ValueKind == JsonValueKind.Array)
      {
        foreach (JsonElement item in element.EnumerateArray())
        {
          if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out value))
          {
            return true;
          }
        }
      }

      value = default;
      return false;
    }

    private static bool TryGetDate(JsonElement element, out DateTimeOffset value)
    {
      if (element.ValueKind == JsonValueKind.String &&
          DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
      {
        return true;
      }

      if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long epoch))
      {
        try
        {
          value = DateTimeOffset.FromUnixTimeSeconds(epoch);
          return true;
        }
        catch (ArgumentOutOfRangeException)
        {
          // Continue searching for a valid date field.
        }
      }

      value = default;
      return false;
    }

    private struct WeatherFields
    {
      public double? Temperature;
      public double? MaximumTemperature;
      public double? MinimumTemperature;
      public double? Precipitation;
      public double? Probability;
      public double? WeatherCode;
      public DateTimeOffset? Time;
      public string? Wind;
      public string? MorningForecast;
      public string? AfternoonForecast;
      public string? NightForecast;
      public string? Summary;
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
        
        var localTime = time.ToOffset(TimeSpan.FromHours(8));
        bool isNight = localTime.Hour < 7 || localTime.Hour > 19;

        if (ContainsIgnoreCase(summary, "ribut petir") || ContainsIgnoreCase(summary, "thunder") || ContainsIgnoreCase(summary, "lightning") || ContainsIgnoreCase(summary, "squall"))
        {
            if (ContainsIgnoreCase(summary, "hail") || ContainsIgnoreCase(summary, "batu")) return "bi-cloud-hail text-warning";
            if (ContainsIgnoreCase(summary, "beberapa tempat") || ContainsIgnoreCase(summary, "satu dua tempat") || ContainsIgnoreCase(summary, "few places") || ContainsIgnoreCase(summary, "isolated")) return "bi-cloud-lightning-fill text-warning";
            return "bi-cloud-lightning-rain-fill text-warning";
        }
        if (ContainsIgnoreCase(summary, "snow") || ContainsIgnoreCase(summary, "salji"))
        {
            if (ContainsIgnoreCase(summary, "heavy") || ContainsIgnoreCase(summary, "lebat")) return "bi-cloud-snow-fill text-primary";
            return "bi-snow text-primary";
        }
        if (ContainsIgnoreCase(summary, "freezing") || ContainsIgnoreCase(summary, "beku")) return "bi-cloud-sleet-fill text-info";
        if (ContainsIgnoreCase(summary, "fog") || ContainsIgnoreCase(summary, "kabus") || ContainsIgnoreCase(summary, "haze") || ContainsIgnoreCase(summary, "rime")) return "bi-cloud-fog2-fill text-secondary";
        if (ContainsIgnoreCase(summary, "heavy rain") || ContainsIgnoreCase(summary, "violent") || ContainsIgnoreCase(summary, "hujan lebat")) return "bi-cloud-rain-heavy-fill text-primary";
        if (ContainsIgnoreCase(summary, "moderate rain") || ContainsIgnoreCase(summary, "hujan sederhana")) return "bi-cloud-rain-fill text-primary";
        if (ContainsIgnoreCase(summary, "slight rain") || ContainsIgnoreCase(summary, "hujan renyai") || ContainsIgnoreCase(summary, "beberapa tempat")) return "bi-cloud-drizzle-fill text-info";
        if (ContainsIgnoreCase(summary, "drizzle") || ContainsIgnoreCase(summary, "gerimis"))
        {
            if (ContainsIgnoreCase(summary, "light") || ContainsIgnoreCase(summary, "ringan")) return "bi-cloud-drizzle text-info";
            return "bi-cloud-drizzle-fill text-info";
        }
        if (ContainsIgnoreCase(summary, "hujan") || ContainsIgnoreCase(summary, "rain") || ContainsIgnoreCase(summary, "shower")) return "bi-cloud-rain-fill text-info";
        if (ContainsIgnoreCase(summary, "overcast") || ContainsIgnoreCase(summary, "mendung")) return "bi-clouds-fill text-secondary";
        if (ContainsIgnoreCase(summary, "partly cloudy") || ContainsIgnoreCase(summary, "separa mendung") || ContainsIgnoreCase(summary, "cloudy")) return isNight ? "bi-cloud-moon-fill text-secondary" : "bi-cloud-sun-fill text-primary";
        if (ContainsIgnoreCase(summary, "clear") || ContainsIgnoreCase(summary, "tiada hujan") || ContainsIgnoreCase(summary, "fair") || ContainsIgnoreCase(summary, "sunny") || ContainsIgnoreCase(summary, "cerah")) return isNight ? "bi-moon-stars-fill text-primary" : "bi-sun-fill text-warning";
        return isNight ? "bi-cloud-moon-fill text-secondary" : "bi-cloud-sun-fill text-primary";
    }

    private static bool ContainsIgnoreCase(string value, string candidate) =>
      value.Contains(candidate, StringComparison.OrdinalIgnoreCase);
  }
}
