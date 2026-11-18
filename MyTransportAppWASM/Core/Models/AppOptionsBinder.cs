using System.Globalization;

namespace MyTransportAppWASM.Core.Models
{
  internal static class AppOptionsBinder
  {
    public static CacheExpirationOptions BindCacheExpirationOptions(IConfigurationSection section)
    {
      var weather = section.GetSection("Weather");
      var map = section.GetSection("Map");
      return new CacheExpirationOptions
      {
        SizeLimit = ParseInt(section["SizeLimit"], 200),
        Weather = new WeatherCacheExpirationOptions
        {
          LiveSeconds = ParseInt(weather["LiveSeconds"], 0),
          OutlookSeconds = ParseInt(weather["OutlookSeconds"], 0),
          FallbackSeconds = ParseInt(weather["FallbackSeconds"], 0)
        },
        Map = new MapCacheExpirationOptions
        {
          GeolocationSeconds = ParseInt(map["GeolocationSeconds"], 0)
        }
      };
    }

    public static DefaultLocationOptions BindDefaultLocationOptions(IConfigurationSection section) => new()
    {
      Latitude = ParseNullableDouble(section["Latitude"]),
      Longitude = ParseNullableDouble(section["Longitude"])
    };

    public static TransitOptions BindTransitOptions(IConfiguration configuration)
    {
      var pollingSection = configuration.GetSection(TransitPollingOptions.SectionName);
      var interval = Math.Max(5, ParseInt(pollingSection["IntervalSeconds"], 15));
      var polling = new TransitPollingOptions
      {
        IntervalSeconds = interval,
        MaximumBackoffSeconds = Math.Max(interval, ParseInt(pollingSection["MaximumBackoffSeconds"], 120)),
        MaximumSnapshotAgeSeconds = Math.Max(interval, ParseInt(pollingSection["MaximumSnapshotAgeSeconds"], 120))
      };

      var providers = new List<TransportProvider>();
      foreach (var child in configuration.GetSection("TransportProviders").GetChildren())
      {
        var staticSection = child.GetSection("StaticData");
        TransportStaticData? staticData = staticSection.Exists()
          ? new TransportStaticData(
            staticSection["BaseUrl"] ?? string.Empty,
            staticSection["RouteInfoPath"] ?? string.Empty,
            staticSection["GeoJSONUrlPath"] ?? string.Empty,
            staticSection["DeparturePath"] ?? string.Empty)
          : null;

        providers.Add(new TransportProvider(
          child["Name"] ?? string.Empty,
          child["Endpoint"] ?? string.Empty,
          ParseDouble(child["CenterLat"], 0),
          ParseDouble(child["CenterLng"], 0),
          ParseDouble(child["RadiusKm"], 0),
          child["TransportType"] ?? "Bus",
          staticData));
      }

      return new TransitOptions { Providers = providers, Polling = polling };
    }

    public static WeatherOptions BindWeatherOptions(IConfigurationSection section) => new()
    {
      MetMalaysia = BindMetMalaysiaProvider(section.GetSection("MetMalaysia")),
      OpenMeteo = BindKeyedProvider(section.GetSection("OpenMeteo")),
      SingaporeNEA = BindKeyedProvider(section.GetSection("SingaporeNEA"))
    };

    public static FirebaseOptions BindFirebaseOptions(IConfigurationSection section) => new()
    {
      ApiKey = section["apiKey"],
      AuthDomain = section["authDomain"],
      ProjectId = section["projectId"],
      StorageBucket = section["storageBucket"],
      MessagingSenderId = section["messagingSenderId"],
      AppId = section["appId"],
      MeasurementId = section["measurementId"],
      GoogleClientId = section["googleClientId"],
      WebPushVapidKey = section["webPushVapidKey"]
    };

    private static MetMalaysiaWeatherProviderOptions BindMetMalaysiaProvider(IConfigurationSection section) =>
      BindProviderOptions(section, () => new MetMalaysiaWeatherProviderOptions
      {
        Endpoints = section["Endpoints"] ?? string.Empty
      });

    private static KeyedWeatherProviderOptions BindKeyedProvider(IConfigurationSection section)
    {
      var endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var entry in section.GetSection("Endpoints").GetChildren())
      {
        endpoints[entry.Key] = entry.Value ?? string.Empty;
      }

      return BindProviderOptions(section, () => new KeyedWeatherProviderOptions
      {
        Endpoints = endpoints
      });
    }

    private static T BindProviderOptions<T>(IConfigurationSection section, Func<T> create)
      where T : WeatherProviderOptions
    {
      var options = create();
      options.Name = section["Name"] ?? string.Empty;
      options.Source = section["Source"] ?? string.Empty;
      options.BaseUrl = section["BaseUrl"] ?? string.Empty;
      options.ApiKey = section["ApiKey"];
      options.Enabled = ParseBool(section["Enabled"]);
      return options;
    }

    private static int ParseInt(string? value, int fallback) =>
      int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ParseDouble(string? value, double fallback) =>
      double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double? ParseNullableDouble(string? value) =>
      double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool ParseBool(string? value) =>
      bool.TryParse(value, out var parsed) && parsed;
  }
}
