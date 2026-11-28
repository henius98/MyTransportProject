using MyTransportAppWASM.Core.Utils;

namespace MyTransportAppWASM.Core.Models;

public sealed class CacheExpirationOptions
{
  public const string SectionName = "CacheExpiration";

  public WeatherCacheExpirationOptions Weather { get; set; } = new();
  public MapCacheExpirationOptions Map { get; set; } = new();
}

public sealed class WeatherCacheExpirationOptions
{
  public int LiveSeconds { get; set; }
  public int OutlookSeconds { get; set; }
  public int FallbackSeconds { get; set; }
}

public sealed class MapCacheExpirationOptions
{
  public const int DefaultStaticDataResetHour = 4;

  public int GeolocationSeconds { get; set; }
  public int StaticDataResetHour { get; set; } = DefaultStaticDataResetHour;

  public DateTime GetStaticDataServiceDay(DateTimeOffset utcNow)
  {
    var resetHour = Math.Clamp(StaticDataResetHour, 0, 23);
    return MalaysiaTime.Convert(utcNow).AddHours(-resetHour).Date;
  }
}
