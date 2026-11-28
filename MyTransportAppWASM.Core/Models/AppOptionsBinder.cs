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
        Weather = new WeatherCacheExpirationOptions
        {
          LiveSeconds = ParseInt(weather["LiveSeconds"], 0),
          OutlookSeconds = ParseInt(weather["OutlookSeconds"], 0),
          FallbackSeconds = ParseInt(weather["FallbackSeconds"], 0)
        },
        Map = new MapCacheExpirationOptions
        {
          GeolocationSeconds = ParseInt(map["GeolocationSeconds"], 0),
          StaticDataResetHour = Math.Clamp(
            ParseInt(map["StaticDataResetHour"], MapCacheExpirationOptions.DefaultStaticDataResetHour),
            0,
            23)
        }
      };
    }

    public static DefaultLocationOptions BindDefaultLocationOptions(IConfigurationSection section) => new()
    {
      Latitude = ParseNullableDouble(section["Latitude"]),
      Longitude = ParseNullableDouble(section["Longitude"])
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

    internal static int ParseInt(string? value, int fallback) =>
      int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    internal static double ParseDouble(string? value, double fallback) =>
      double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    internal static double? ParseNullableDouble(string? value) =>
      double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    internal static bool ParseBool(string? value) =>
      bool.TryParse(value, out var parsed) && parsed;
  }
}
