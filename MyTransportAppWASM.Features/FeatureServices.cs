using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MyTransportAppWASM.Core.Auth;
using MyTransportAppWASM.Core.Models;
using MyTransportAppWASM.Core.Services;
using MyTransportAppWASM.Features.Fortune.Services;
using MyTransportAppWASM.Features.Google.Services;
using MyTransportAppWASM.Features.Transit.Models;
using MyTransportAppWASM.Features.Transit.Services;
using MyTransportAppWASM.Features.Weather.Models;
using MyTransportAppWASM.Features.Weather.Services;

namespace MyTransportAppWASM.Features;

/// <summary>
/// Lazily provisions feature DI services on first route load without pulling feature assemblies into startup Program.Main.
/// </summary>
internal static class FeatureServices
{
  private const string WeatherPlannerClientKey = "WeatherPlanner";
  private const string BaziFlowClientKey = "BaziFlow";

  private static readonly HashSet<Type> TransientComponentServices =
  [
    typeof(TransitMapSession),
    typeof(GoogleCalendarWorkflowService)
  ];

  public static T Resolve<T>(IServiceProvider rootServices) where T : notnull
  {
    // Component rendering tests register mock feature services directly on the root container.
    var existing = rootServices.GetService<T>();
    if (existing is not null)
    {
      return existing;
    }

    var host = rootServices.GetRequiredService<FeatureServiceHost>();
    var featureProvider = host.GetOrCreate(() => BuildProvider(rootServices));

    if (TransientComponentServices.Contains(typeof(T)))
    {
      // Caller owns disposal of per-component transient instances so the container never retains them.
      return ActivatorUtilities.CreateInstance<T>(featureProvider);
    }

    return featureProvider.GetRequiredService<T>();
  }

  private static ServiceProvider BuildProvider(IServiceProvider root)
  {
    var services = new ServiceCollection();
    var config = root.GetRequiredService<IConfiguration>();
    var transitOptions = FeatureOptionsBinder.BindTransitOptions(config);
    var weatherOptions = FeatureOptionsBinder.BindWeatherOptions(config.GetSection("WeatherProviders"));

    services.AddSingleton(config);
    services.AddSingleton(root.GetRequiredService<IJSRuntime>());
    services.AddSingleton(root.GetRequiredService<ILoggerFactory>());
    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    services.AddSingleton(root.GetRequiredService<ILocationService>());
    services.AddSingleton(root.GetRequiredService<IBrowserStorageService>());
    services.AddSingleton(root.GetRequiredService<FirebaseClientService>());
    services.AddSingleton(root.GetRequiredService<IOptions<DefaultLocationOptions>>());
    services.AddSingleton(root.GetRequiredService<IOptions<CacheExpirationOptions>>());
    services.AddSingleton(Options.Create(transitOptions));
    services.AddSingleton(Options.Create(transitOptions.Polling));
    services.AddSingleton(Options.Create(weatherOptions));

    var httpClients = root.GetRequiredService<IAppHttpClientFactory>();

    services.AddKeyedSingleton<HttpClient>(WeatherPlannerClientKey, (_, _) => CreateExternalClient(
      new WeatherRateLimitingHandler { InnerHandler = httpClients.CreateExternalHandler() }));
    services.AddKeyedSingleton<HttpClient>(BaziFlowClientKey, (sp, _) =>
    {
      var storage = sp.GetRequiredService<IBrowserStorageService>();
      Uri.TryCreate(config["BaziFlow:BaseUrl"], UriKind.Absolute, out var baziUri);
      return CreateExternalClient(
        new BaziFlowAuthHandler(storage, config) { InnerHandler = httpClients.CreateExternalHandler() },
        baziUri);
    });

    services.AddSingleton<IGtfsService>(sp => new GtfsService(
      httpClients.ExternalApi,
      sp.GetRequiredService<ILogger<GtfsService>>()));
    services.AddSingleton<ITransportStaticDataService>(_ => new TransportStaticDataService(
      httpClients.StaticAssets));
    services.AddSingleton<IWeatherStationCatalogService>(_ => new WeatherStationCatalogService(
      httpClients.StaticAssets));
    services.AddSingleton<IPlaceResolutionService, PlaceResolutionService>();
    services.AddSingleton<IWeatherPlannerService>(sp => new WeatherPlannerService(
      sp.GetRequiredKeyedService<HttpClient>(WeatherPlannerClientKey),
      sp.GetRequiredService<IOptions<CacheExpirationOptions>>(),
      sp.GetRequiredService<IOptions<WeatherOptions>>(),
      sp.GetRequiredService<IPlaceResolutionService>(),
      sp.GetRequiredService<IWeatherStationCatalogService>()));
    services.AddSingleton<IBaziFlowService>(sp => new BaziFlowService(
      sp.GetRequiredKeyedService<HttpClient>(BaziFlowClientKey),
      sp.GetRequiredService<IBrowserStorageService>(),
      sp.GetRequiredService<ILogger<BaziFlowService>>()));
    services.AddSingleton<ITransitTrackingService, TransitTrackingService>();
    services.AddSingleton<IGoogleWorkspaceService, GoogleWorkspaceService>();
    services.AddSingleton<GoogleTasksWorkflowService>();

    return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
  }

  private static HttpClient CreateExternalClient(HttpMessageHandler handler, Uri? baseAddress = null) =>
    new(handler, disposeHandler: true)
    {
      Timeout = HttpClientDefaults.ExternalTimeout,
      BaseAddress = baseAddress
    };
}
