using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Authorization;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Services.Handlers;

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

      builder.Services.Configure<WeatherOptions>(builder.Configuration.GetSection("WeatherProviders"));
      builder.Services.Configure<CacheExpirationOptions>(builder.Configuration.GetSection(CacheExpirationOptions.SectionName));
      builder.Services.AddMemoryCache(options =>
      {
        var configuredLimit = builder.Configuration.GetValue<int>($"{CacheExpirationOptions.SectionName}:SizeLimit");
        options.SizeLimit = configuredLimit > 0 ? configuredLimit : 200;
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
      builder.Services.AddScoped<ILiveRoutingService, LiveRoutingService>();
      builder.Services.AddScoped<ITransitTrackingService, TransitTrackingService>();

      builder.Services.AddAuthorizationCore();
      builder.Services.AddCascadingAuthenticationState();
      builder.Services.AddScoped<AuthenticationStateProvider, FirebaseAuthenticationStateProvider>();
      builder.Services.AddScoped<IGoogleWorkspaceService, GoogleWorkspaceService>();
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
  }
}
