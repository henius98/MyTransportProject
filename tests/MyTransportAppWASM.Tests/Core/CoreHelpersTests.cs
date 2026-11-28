using Microsoft.Extensions.Configuration;

namespace MyTransportAppWASM.Tests;

public class CoreHelpersTests
{
  [Theory]
  [InlineData("map", "map")]
  [InlineData("/map", "map")]
  [InlineData("/map/?tab=planner", "map")]
  [InlineData("/weather/?q=kl", "weather")]
  [InlineData("/map/#top", "map")]
  [InlineData("/fortune/?day=today#section", "fortune")]
  [InlineData("/google/calendar/?view=month", "google/calendar")]
  [InlineData("/google/?q=1", "google")]
  public void TrimRoutePath_SlicesQueryAndHashBeforeTrimmingSlashes(string path, string expected)
  {
    Assert.Equal(expected, App.TrimRoutePath(path).ToString());
  }

  [Theory]
  [InlineData("map", false, true)]
  [InlineData("/map", false, true)]
  [InlineData("/map/?tab=planner", false, true)]
  [InlineData("/map/#top", false, true)]
  [InlineData("/MAP?tab=planner#top", false, true)]
  [InlineData("map", true, false)]
  [InlineData("weather", false, false)]
  [InlineData("", false, false)]
  [InlineData(null, false, false)]
  public void ShouldLoadGtfsAssembly_MatchesMapRouteOnlyWhenNotYetLoaded(
    string? path,
    bool alreadyLoaded,
    bool expected)
  {
    Assert.Equal(expected, App.ShouldLoadGtfsAssembly(path, alreadyLoaded));
  }

  [Theory]
  [InlineData("weather", false, true)]
  [InlineData("/weather/?q=kl", false, true)]
  [InlineData("/map?q=test", false, true)]
  [InlineData("/map/?tab=planner", false, true)]
  [InlineData("fortune", false, true)]
  [InlineData("google/calendar", false, true)]
  [InlineData("/google/calendar/?view=month", false, true)]
  [InlineData("google/tasks", false, true)]
  [InlineData("google", false, false)]
  [InlineData("/google/", false, false)]
  [InlineData("/google/?q=1", false, false)]
  [InlineData("privacy", false, false)]
  [InlineData("", false, false)]
  [InlineData("weather", true, false)]
  public void ShouldLoadFeaturesAssembly_MatchesFeatureRoutesOnlyWhenNotYetLoaded(
    string path,
    bool alreadyLoaded,
    bool expected)
  {
    Assert.Equal(expected, App.ShouldLoadFeaturesAssembly(path, alreadyLoaded));
  }

  [Theory]
  [InlineData("https://app.example/weather", true)]
  [InlineData("/google/calendar", true)]
  [InlineData("https://evil.example/phish", false)]
  [InlineData("javascript:alert(1)", false)]
  [InlineData("", false)]
  [InlineData(null, false)]
  public void IsSafeReturnUrl_AllowsSameOriginOnly(string? returnUrl, bool expected)
  {
    const string baseUri = "https://app.example/";
    Uri ToAbsolute(string value) => new(new Uri(baseUri), value);

    Assert.Equal(expected, AuthLoginHelpers.IsSafeReturnUrl(returnUrl, baseUri, ToAbsolute));
  }

  [Fact]
  public void MapSignInError_DistinguishesPopupClosedBlockedAndUnauthorized()
  {
    Assert.Equal((null, false), AuthLoginHelpers.MapSignInError("Firebase: auth/popup-closed-by-user"));

    var (blockedMsg, blockedLog) = AuthLoginHelpers.MapSignInError("auth/popup-blocked");
    Assert.Contains("pop-up was blocked", blockedMsg);
    Assert.True(blockedLog);

    var (domainMsg, domainLog) = AuthLoginHelpers.MapSignInError("auth/unauthorized-domain");
    Assert.Contains("not authorized", domainMsg);
    Assert.True(domainLog);

    var (fallbackMsg, fallbackLog) = AuthLoginHelpers.MapSignInError("network error");
    Assert.Equal("Failed to sign in. Please try again.", fallbackMsg);
    Assert.True(fallbackLog);
  }

  [Fact]
  public void AppOptionsBinder_EnforcesPollingBoundsAndBindsProviderConfig()
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["CacheExpiration:Weather:LiveSeconds"] = "300",
        ["TransitPolling:IntervalSeconds"] = "2",
        ["TransitPolling:MaximumBackoffSeconds"] = "3",
        ["TransitPolling:MaximumSnapshotAgeSeconds"] = "4",
        ["TransportProviders:0:Name"] = "Rapid KL",
        ["TransportProviders:0:Endpoint"] = "https://api.data.gov.my/gtfs-realtime/vehicle-position/prasarana?category=rapid-bus-kl",
        ["TransportProviders:0:CenterLat"] = "3.139",
        ["TransportProviders:0:CenterLng"] = "101.6869",
        ["TransportProviders:0:RadiusKm"] = "30",
        ["TransportProviders:0:StaticData:BaseUrl"] = "https://static.example",
        ["WeatherProviders:OpenMeteo:Name"] = "Open-Meteo",
        ["WeatherProviders:OpenMeteo:Enabled"] = "true",
        ["WeatherProviders:OpenMeteo:Endpoints:Realtime"] = "/v1/forecast"
      })
      .Build();

    var cache = AppOptionsBinder.BindCacheExpirationOptions(config.GetSection(CacheExpirationOptions.SectionName));
    var transit = FeatureOptionsBinder.BindTransitOptions(config);
    var weather = FeatureOptionsBinder.BindWeatherOptions(config.GetSection("WeatherProviders"));

    Assert.Equal(300, cache.Weather.LiveSeconds);
    Assert.Equal(MapCacheExpirationOptions.DefaultStaticDataResetHour, cache.Map.StaticDataResetHour);
    Assert.Equal(5, transit.Polling.IntervalSeconds);
    Assert.Equal(5, transit.Polling.MaximumBackoffSeconds);
    Assert.Equal(5, transit.Polling.MaximumSnapshotAgeSeconds);
    var provider = Assert.Single(transit.Providers);
    Assert.Equal("Rapid KL", provider.Name);
    Assert.NotNull(provider.StaticData);
    Assert.True(weather.OpenMeteo.Enabled);
    Assert.Equal("/v1/forecast", weather.OpenMeteo.Endpoints["Realtime"]);
  }

  [Fact]
  public void MapCacheExpirationOptions_ComputesServiceDayAndClampsResetHourInMalaysiaTime()
  {
    var options = new MapCacheExpirationOptions { StaticDataResetHour = 4 };

    // Service day rolls over at 04:00 MYT (03:59:59 still belongs to previous service day).
    var daytime = new DateTimeOffset(2026, 11, 26, 11, 15, 0, MalaysiaTime.Offset);
    var justBeforeReset = new DateTimeOffset(2026, 11, 27, 3, 59, 59, MalaysiaTime.Offset);
    var exactReset = new DateTimeOffset(2026, 11, 27, 4, 0, 0, MalaysiaTime.Offset);
    Assert.Equal(new DateTime(2026, 11, 26), options.GetStaticDataServiceDay(daytime.ToUniversalTime()));
    Assert.Equal(new DateTime(2026, 11, 26), options.GetStaticDataServiceDay(justBeforeReset.ToUniversalTime()));
    Assert.Equal(new DateTime(2026, 11, 27), options.GetStaticDataServiceDay(exactReset.ToUniversalTime()));

    // Out-of-range config values clamp safely to 0..23.
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["CacheExpiration:Map:StaticDataResetHour"] = "30"
      })
      .Build();
    var bound = AppOptionsBinder.BindCacheExpirationOptions(config.GetSection(CacheExpirationOptions.SectionName));
    Assert.Equal(23, bound.Map.StaticDataResetHour);
  }
}
