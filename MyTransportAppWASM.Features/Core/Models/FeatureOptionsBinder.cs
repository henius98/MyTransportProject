using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Features.Transit.Models;
using MyTransportAppWASM.Features.Weather.Models;

namespace MyTransportAppWASM.Core.Models
{
  internal static class FeatureOptionsBinder
  {
    public static TransitOptions BindTransitOptions(IConfiguration configuration)
    {
      var pollingSection = configuration.GetSection(TransitPollingOptions.SectionName);
      var interval = Math.Max(5, AppOptionsBinder.ParseInt(pollingSection["IntervalSeconds"], 15));
      var polling = new TransitPollingOptions
      {
        IntervalSeconds = interval,
        MaximumBackoffSeconds = Math.Max(interval, AppOptionsBinder.ParseInt(pollingSection["MaximumBackoffSeconds"], 120)),
        MaximumSnapshotAgeSeconds = Math.Max(interval, AppOptionsBinder.ParseInt(pollingSection["MaximumSnapshotAgeSeconds"], 120))
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
            staticSection["FrequenciesPath"] ?? string.Empty)
          : null;

        providers.Add(new TransportProvider(
          child["Name"] ?? string.Empty,
          child["Endpoint"] ?? string.Empty,
          AppOptionsBinder.ParseDouble(child["CenterLat"], 0),
          AppOptionsBinder.ParseDouble(child["CenterLng"], 0),
          AppOptionsBinder.ParseDouble(child["RadiusKm"], 0),
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
      options.Enabled = AppOptionsBinder.ParseBool(section["Enabled"]);
      return options;
    }
  }
}
