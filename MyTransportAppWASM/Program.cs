using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
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

      var cacheExpiration = AppOptionsBinder.BindCacheExpirationOptions(builder.Configuration.GetSection(CacheExpirationOptions.SectionName));
      builder.Services.AddSingleton(Options.Create(cacheExpiration));
      builder.Services.AddSingleton(Options.Create(AppOptionsBinder.BindDefaultLocationOptions(builder.Configuration.GetSection(DefaultLocationOptions.SectionName))));

      // Browser requests already share the browser's connection pool. Keep failures
      // bounded without pulling a server-oriented retry/circuit-breaker stack into WASM.
      builder.Services.AddSingleton<IAppHttpClientFactory>(_ =>
        new AppHttpClientFactory(new Uri(builder.HostEnvironment.BaseAddress)));

      builder.Services.AddScoped<ThemeService>();
      builder.Services.AddCascadingValue(sp =>
      {
        var theme = sp.GetRequiredService<ThemeService>();
        var source = new CascadingValueSource<ThemeService>(theme, isFixed: false);
        theme.OnThemeChanged += () => _ = source.NotifyChangedAsync();
        return source;
      });
      builder.Services.AddScoped<LanguageService>(services => new LanguageService(
          services.GetRequiredService<IAppHttpClientFactory>().StaticAssets));
      builder.Services.AddCascadingValue(sp =>
      {
        var language = sp.GetRequiredService<LanguageService>();
        var source = new CascadingValueSource<LanguageService>(language, isFixed: false);
        language.OnLanguageChanged += () => _ = source.NotifyChangedAsync();
        return source;
      });
      builder.Services.AddScoped<ILocationService, LocationService>();

      builder.Services.AddSingleton(Options.Create(AppOptionsBinder.BindFirebaseOptions(builder.Configuration.GetSection("Firebase"))));
      builder.Services.AddScoped<FirebaseClientService>();
      builder.Services.AddScoped<FirebaseAuthSessionService>();
      builder.Services.AddScoped<IAuthSessionService>(sp => sp.GetRequiredService<FirebaseAuthSessionService>());
      builder.Services.AddCascadingValue(sp =>
      {
        var auth = sp.GetRequiredService<IAuthSessionService>();
        var source = new CascadingValueSource<AuthSessionState>(auth.CurrentState, isFixed: false);
        auth.StateChanged += state => _ = source.NotifyChangedAsync(state);
        _ = auth.GetStateAsync();
        return source;
      });
      builder.Services.AddScoped<IUserSettingsService, UserSettingsService>();
      builder.Services.AddScoped<AppStateService>();
      builder.Services.AddScoped<FeatureServiceHost>();

      var host = builder.Build();

      // Load embedded en-US translations synchronously so App mounts without waiting on HTTP.
      var languageService = host.Services.GetRequiredService<LanguageService>();
      languageService.LoadDefaultLanguage();

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
