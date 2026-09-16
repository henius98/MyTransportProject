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
      builder.Services.AddMemoryCache();

      builder.Services.AddSingleton<IFormFactor, FormFactor>();

      // Browser requests already share the browser's connection pool. Keep failures
      // bounded without pulling a server-oriented retry/circuit-breaker stack into WASM.
      static void ConfigureExternalApiClient(HttpClient client) =>
        client.Timeout = TimeSpan.FromSeconds(15);

      builder.Services.AddHttpClient<IGtfsService, GtfsService>(ConfigureExternalApiClient);

      builder.Services.AddTransient<WeatherRateLimitingHandler>();
      builder.Services.AddHttpClient<IWeatherPlannerService, WeatherPlannerService>(ConfigureExternalApiClient)
          .AddHttpMessageHandler<WeatherRateLimitingHandler>();

      builder.Services.AddHttpClient("StaticAssets", client =>
      {
        client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
        client.Timeout = TimeSpan.FromSeconds(15);
      });
      builder.Services.AddScoped<IGeocodingService>(services => new GeocodingService(
          services.GetRequiredService<IHttpClientFactory>().CreateClient("StaticAssets"),
          services.GetRequiredService<IConfiguration>()));

      builder.Services.AddTransient<BaziFlowAuthHandler>();
      builder.Services.AddHttpClient<IBaziFlowService, BaziFlowService>(client =>
      {
          client.BaseAddress = new Uri(builder.Configuration["BaziFlow:BaseUrl"] ?? "http://localhost:3000");
          ConfigureExternalApiClient(client);
      })
      .AddHttpMessageHandler<BaziFlowAuthHandler>();

      builder.Services.AddScoped<ThemeService>();
      builder.Services.AddScoped<LanguageService>(services => new LanguageService(
          services.GetRequiredService<IHttpClientFactory>().CreateClient("StaticAssets")));
      builder.Services.AddScoped<ILocationService, LocationService>();
      builder.Services.AddScoped<MyTransportAppWASM.Services.Interfaces.ILiveRoutingService, LiveRoutingService>();
      builder.Services.AddTransient<CountdownTimer>();

      builder.Services.AddAuthorizationCore();
      builder.Services.AddScoped<AuthenticationStateProvider, FirebaseAuthenticationStateProvider>();
      builder.Services.AddScoped<GoogleWorkspaceService>();
      builder.Services.AddScoped<IUserSettingsService, UserSettingsService>();
      builder.Services.AddScoped<AppStateService>();



      var host = builder.Build();

      // Force initialize the LanguageService on boot to ensure initial language is loaded
      var languageService = host.Services.GetRequiredService<LanguageService>();
      await languageService.LoadLanguageAsync("en-US");

      await host.RunAsync();
    }
  }
}
