using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Blazored.LocalStorage;
using System.Reflection;
using System.Globalization;

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

      builder.Services.AddBlazoredLocalStorage();

      builder.Services.Configure<WeatherOptions>(builder.Configuration.GetSection("WeatherProviders"));
      builder.Services.AddMemoryCache();

      builder.Services.AddSingleton<IFormFactor, FormFactor>();

      // Fail-fast resilience pipeline optimized for mobile networks (shorter timeouts, fewer retries)
      Action<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions> failFastResilience = options =>
      {
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.Retry.MaxRetryAttempts = 2;
      };

      builder.Services.AddHttpClient<IGtfsService, GtfsService>()
          .AddStandardResilienceHandler(failFastResilience);

      builder.Services.AddHttpClient<IWeatherPlannerService, WeatherPlannerService>()
          .AddStandardResilienceHandler(failFastResilience);

      builder.Services.AddHttpClient<IGeocodingService, GeocodingService>(client =>
      {
        client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
      })
          .AddStandardResilienceHandler(failFastResilience);

      builder.Services.AddScoped<ThemeService>();
      builder.Services.AddScoped<LanguageService>();
      builder.Services.AddScoped<ILocationService, LocationService>();
      builder.Services.AddTransient<CountdownTimer>();

      builder.Services.AddOidcAuthentication(options =>
      {
        builder.Configuration.Bind("Authentication:Google", options.ProviderOptions);
        options.ProviderOptions.DefaultScopes.Add("openid");
        options.ProviderOptions.DefaultScopes.Add("profile");
        options.ProviderOptions.DefaultScopes.Add("email");
      });

      builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

      var host = builder.Build();

      // Force initialize the LanguageService on boot to ensure initial language is loaded
      var languageService = host.Services.GetRequiredService<LanguageService>();
      await languageService.LoadLanguageAsync("en-US");

      await host.RunAsync();
    }
  }
}
