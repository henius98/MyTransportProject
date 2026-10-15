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
      builder.Services.AddHttpClient<IPlaceResolutionService, PlaceResolutionService>(ConfigureExternalApiClient);

      builder.Services.AddTransient<BaziFlowAuthHandler>();
      builder.Services.AddHttpClient<IBaziFlowService, BaziFlowService>(client =>
      {
          client.BaseAddress = new Uri(builder.Configuration["BaziFlow:BaseUrl"] ?? "http://localhost:8080");
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
      builder.Services.AddScoped<MyTransportAppWASM.Services.Interfaces.ILiveRoutingService, LiveRoutingService>();
      builder.Services.AddScoped<ITransitTrackingService, TransitTrackingService>();

      builder.Services.AddAuthorizationCore();
      builder.Services.AddCascadingAuthenticationState();
      builder.Services.AddScoped<AuthenticationStateProvider, FirebaseAuthenticationStateProvider>();
      builder.Services.AddScoped<GoogleWorkspaceService>();
      builder.Services.AddScoped<IGoogleWorkspaceService>(sp => sp.GetRequiredService<GoogleWorkspaceService>());
      builder.Services.AddScoped<IGoogleTasksWorkflowService, GoogleTasksWorkflowService>();
      builder.Services.AddScoped<IUserSettingsService, UserSettingsService>();
      builder.Services.AddScoped<AppStateService>();
      builder.Services.AddSingleton<IFormFactor, FormFactor>();
      builder.Services.AddTransient<MyTransportAppWASM.Utils.CountdownTimer>();
      builder.Services.AddScoped<IGeocodingService>(sp =>
          new GeocodingService(
              sp.GetRequiredService<IPlaceResolutionService>(),
              sp.GetRequiredService<IWeatherStationCatalogService>()));
      builder.Services.AddScoped<IStateHydrationService>(sp =>
          new StateHydrationService(sp.GetService<Microsoft.AspNetCore.Components.PersistentComponentState>()));



      var host = builder.Build();

      // Force initialize the LanguageService on boot to ensure initial language is loaded
      var languageService = host.Services.GetRequiredService<LanguageService>();
      try
      {
        await languageService.LoadLanguageAsync("en-US");
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Initial language load failed: {ex.Message}");
      }

      await host.RunAsync();
    }
  }
}
