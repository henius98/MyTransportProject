using Microsoft.Extensions.Configuration;

namespace MyTransportAppWASM.Tests;

public class CoreHelpersTests
{
  [Theory]
  [InlineData("map", false, true)]
  [InlineData("/map", false, true)]
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
        ["CacheExpiration:SizeLimit"] = "150",
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
    var transit = AppOptionsBinder.BindTransitOptions(config);
    var weather = AppOptionsBinder.BindWeatherOptions(config.GetSection("WeatherProviders"));

    Assert.Equal(150, cache.SizeLimit);
    Assert.Equal(300, cache.Weather.LiveSeconds);
    Assert.Equal(5, transit.Polling.IntervalSeconds);
    Assert.Equal(5, transit.Polling.MaximumBackoffSeconds);
    Assert.Equal(5, transit.Polling.MaximumSnapshotAgeSeconds);
    var provider = Assert.Single(transit.Providers);
    Assert.Equal("Rapid KL", provider.Name);
    Assert.NotNull(provider.StaticData);
    Assert.True(weather.OpenMeteo.Enabled);
    Assert.Equal("/v1/forecast", weather.OpenMeteo.Endpoints["Realtime"]);
  }
}
