using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM
{
  public class Program
  {
    public static async Task Main(string[] args)
    {
      // Automatically loads appsettings.json and appsettings.{Environment}.json from wwwroot.
      // The framework fetches these via HTTP and merges them into builder.Configuration.
      var builder = WebAssemblyHostBuilder.CreateDefault(args);
      ConfigureLogging(builder.Logging, builder.Configuration);
      builder.RootComponents.Add<App>("#app");
      builder.RootComponents.Add<HeadOutlet>("head::after");

      builder.Services.AddScoped<IBrowserStorageService, BrowserStorageService>();

      var cacheExpiration = BindCacheExpirationOptions(builder.Configuration.GetSection(CacheExpirationOptions.SectionName));
      builder.Services.AddSingleton(Options.Create(BindWeatherOptions(builder.Configuration.GetSection("WeatherProviders"))));
      builder.Services.AddSingleton(Options.Create(cacheExpiration));
      builder.Services.AddSingleton(Options.Create(BindDefaultLocationOptions(builder.Configuration.GetSection(DefaultLocationOptions.SectionName))));
      var transitOptions = BindTransitOptions(builder.Configuration);
      builder.Services.AddSingleton(Options.Create(transitOptions.Polling));
      builder.Services.AddSingleton(Options.Create(transitOptions));
      builder.Services.AddMemoryCache(options =>
      {
        options.SizeLimit = cacheExpiration.SizeLimit > 0 ? cacheExpiration.SizeLimit : 200;
      });


      // Browser requests already share the browser's connection pool. Keep failures
      // bounded without pulling a server-oriented retry/circuit-breaker stack into WASM.
      static void ConfigureExternalApiClient(HttpClient client) =>
        client.Timeout = TimeSpan.FromSeconds(15);

      builder.Services.AddHttpClient<IGtfsService, GtfsService>(ConfigureExternalApiClient);
      builder.Services.AddHttpClient<ITransportStaticDataService, TransportStaticDataService>(ConfigureExternalApiClient);

      builder.Services.AddTransient<WeatherRateLimitingHandler>();
      builder.Services.AddHttpClient<IWeatherPlannerService, WeatherPlannerService>(ConfigureExternalApiClient)
          .AddHttpMessageHandler<WeatherRateLimitingHandler>();

      builder.Services.AddHttpClient("StaticAssets", client =>
      {
        client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
        client.Timeout = TimeSpan.FromSeconds(15);
      });
      builder.Services.AddScoped<IWeatherStationCatalogService>(services => new WeatherStationCatalogService(
          services.GetRequiredService<IHttpClientFactory>().CreateClient("StaticAssets")));
      builder.Services.AddScoped<IPlaceResolutionService, PlaceResolutionService>();

      builder.Services.AddTransient<BaziFlowAuthHandler>();
      builder.Services.AddHttpClient<IBaziFlowService, BaziFlowService>(client =>
      {
          var baziBaseUrl = builder.Configuration["BaziFlow:BaseUrl"];
          if (Uri.TryCreate(baziBaseUrl, UriKind.Absolute, out var baziUri))
          {
              client.BaseAddress = baziUri;
          }
          ConfigureExternalApiClient(client);
      })
      .AddHttpMessageHandler<BaziFlowAuthHandler>();

      builder.Services.AddScoped<ThemeService>();
      builder.Services.AddCascadingValue(sp =>
      {
        var theme = sp.GetRequiredService<ThemeService>();
        var source = new CascadingValueSource<ThemeService>(theme, isFixed: false);
        theme.OnThemeChanged += () => _ = source.NotifyChangedAsync();
        return source;
      });
      builder.Services.AddScoped<LanguageService>(services => new LanguageService(
          services.GetRequiredService<IHttpClientFactory>().CreateClient("StaticAssets")));
      builder.Services.AddCascadingValue(sp =>
      {
        var language = sp.GetRequiredService<LanguageService>();
        var source = new CascadingValueSource<LanguageService>(language, isFixed: false);
        language.OnLanguageChanged += () => _ = source.NotifyChangedAsync();
        return source;
      });
      builder.Services.AddScoped<ILocationService, LocationService>();
      builder.Services.AddScoped<ITransitTrackingService, TransitTrackingService>();
      builder.Services.AddTransient<TransitMapSession>();

      builder.Services.AddSingleton(Options.Create(BindFirebaseOptions(builder.Configuration.GetSection("Firebase"))));
      builder.Services.AddScoped<FirebaseClientService>();
      builder.Services.AddAuthorizationCore();
      builder.Services.AddCascadingAuthenticationState();
      builder.Services.AddScoped<AuthenticationStateProvider, FirebaseAuthenticationStateProvider>();
      builder.Services.AddScoped<IGoogleWorkspaceService, GoogleWorkspaceService>();
      builder.Services.AddTransient<GoogleCalendarWorkflowService>();
      builder.Services.AddScoped<IGoogleTasksWorkflowService, GoogleTasksWorkflowService>();
      builder.Services.AddScoped<IUserSettingsService, UserSettingsService>();
      builder.Services.AddScoped<AppStateService>();

      var host = builder.Build();

      // Force initialize the LanguageService on boot to ensure initial language is loaded
      var languageService = host.Services.GetRequiredService<LanguageService>();
      try
      {
        await languageService.LoadLanguageAsync("en-US");
      }
      catch (Exception ex)
      {
        host.Services.GetRequiredService<ILoggerFactory>()
          .CreateLogger<Program>()
          .LogWarning("Initial language load failed: {Message}", ex.Message);
      }

      await host.RunAsync();
    }

    private static void ConfigureLogging(ILoggingBuilder logging, IConfiguration configuration)
    {
      logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

      var logLevelSection = configuration.GetSection("Logging:LogLevel");
      if (Enum.TryParse<LogLevel>(logLevelSection["Default"], ignoreCase: true, out var defaultLevel))
      {
        logging.SetMinimumLevel(defaultLevel);
      }

      foreach (var entry in logLevelSection.GetChildren())
      {
        if (!string.Equals(entry.Key, "Default", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<LogLevel>(entry.Value, ignoreCase: true, out var categoryLevel))
        {
          logging.AddFilter(entry.Key, categoryLevel);
        }
      }
    }

    private static CacheExpirationOptions BindCacheExpirationOptions(IConfigurationSection section)
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

    private static DefaultLocationOptions BindDefaultLocationOptions(IConfigurationSection section) => new()
    {
      Latitude = ParseNullableDouble(section["Latitude"]),
      Longitude = ParseNullableDouble(section["Longitude"])
    };

    private static TransitOptions BindTransitOptions(IConfiguration configuration)
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

    private static WeatherOptions BindWeatherOptions(IConfigurationSection section) => new()
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
      options.Enabled = ParseBool(section["Enabled"]);
      return options;
    }

    private static FirebaseOptions BindFirebaseOptions(IConfigurationSection section) => new()
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
