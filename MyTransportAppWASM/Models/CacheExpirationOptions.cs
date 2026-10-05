namespace MyTransportAppWASM.Models;

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
  public int GeolocationSeconds { get; set; }
}
