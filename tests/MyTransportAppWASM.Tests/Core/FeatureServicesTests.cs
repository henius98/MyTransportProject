using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using MyTransportAppWASM.Features;

namespace MyTransportAppWASM.Tests;

public class FeatureServicesTests
{
  [Fact]
  public async Task FeatureServices_ResolvesAllLazyFeatureServicesFromCoreContainer()
  {
    await using var provider = CreateCoreFeatureProvider().BuildServiceProvider();

    Assert.NotNull(FeatureServices.Resolve<IGtfsService>(provider));
    Assert.NotNull(FeatureServices.Resolve<ITransportStaticDataService>(provider));
    Assert.NotNull(FeatureServices.Resolve<ITransitTrackingService>(provider));
    Assert.NotNull(FeatureServices.Resolve<IWeatherStationCatalogService>(provider));
    Assert.NotNull(FeatureServices.Resolve<IPlaceResolutionService>(provider));
    Assert.NotNull(FeatureServices.Resolve<IWeatherPlannerService>(provider));
    Assert.NotNull(FeatureServices.Resolve<IBaziFlowService>(provider));
    Assert.NotNull(FeatureServices.Resolve<IGoogleWorkspaceService>(provider));
    Assert.NotNull(FeatureServices.Resolve<GoogleCalendarWorkflowService>(provider));
    Assert.NotNull(FeatureServices.Resolve<GoogleTasksWorkflowService>(provider));

    using var session1 = FeatureServices.Resolve<TransitMapSession>(provider);
    using var session2 = FeatureServices.Resolve<TransitMapSession>(provider);
    Assert.NotSame(session1, session2);

    var tracking1 = FeatureServices.Resolve<ITransitTrackingService>(provider);
    var tracking2 = FeatureServices.Resolve<ITransitTrackingService>(provider);
    Assert.Same(tracking1, tracking2);
  }

  [Fact]
  public async Task FeatureServices_DoesNotRetainTransientTransitMapSessionAndDisposesOwnedResources()
  {
    var createdHandlers = new List<TrackingHttpMessageHandler>();
    var services = CreateCoreFeatureProvider(() =>
    {
      var handler = new TrackingHttpMessageHandler();
      createdHandlers.Add(handler);
      return handler;
    });

    var provider = services.BuildServiceProvider();
    var tracking = FeatureServices.Resolve<ITransitTrackingService>(provider);
    Assert.NotNull(FeatureServices.Resolve<IWeatherPlannerService>(provider));
    Assert.NotNull(FeatureServices.Resolve<IBaziFlowService>(provider));
    Assert.Equal(4, createdHandlers.Count);

    var sessionRef = CreateAndDisposeTransientSession(provider);
    for (var i = 0; i < 3; i++)
    {
      GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
      GC.WaitForPendingFinalizers();
      GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
      if (!IsTargetAlive(sessionRef))
      {
        break;
      }
    }

    Assert.False(IsTargetAlive(sessionRef));
    GC.KeepAlive(provider);

    await provider.DisposeAsync();

    Assert.All(createdHandlers, handler => Assert.True(handler.Disposed));
    await Assert.ThrowsAsync<ObjectDisposedException>(() => tracking.RefreshVehiclesAsync(3.139, 101.6869));
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static WeakReference<TransitMapSession> CreateAndDisposeTransientSession(IServiceProvider provider)
  {
    var session = FeatureServices.Resolve<TransitMapSession>(provider);
    session.Dispose();
    return new WeakReference<TransitMapSession>(session);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static bool IsTargetAlive(WeakReference<TransitMapSession> reference) =>
    reference.TryGetTarget(out _);

  private static ServiceCollection CreateCoreFeatureProvider(Func<HttpMessageHandler>? handlerFactory = null)
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["BaziFlow:BaseUrl"] = "https://bazi.example/"
      })
      .Build();

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(config);
    services.AddSingleton(new Mock<IJSRuntime>().Object);
    services.AddSingleton(new Mock<ILocationService>().Object);
    services.AddSingleton(new Mock<IBrowserStorageService>().Object);
    services.AddSingleton(Options.Create(new FirebaseOptions()));
    services.AddScoped<FirebaseClientService>();
    services.AddScoped<FeatureServiceHost>();
    services.AddSingleton(Options.Create(new DefaultLocationOptions { Latitude = 3.139, Longitude = 101.6869 }));
    services.AddSingleton(Options.Create(new CacheExpirationOptions
    {
      Weather = new WeatherCacheExpirationOptions
      {
        LiveSeconds = 600,
        OutlookSeconds = 10800,
        FallbackSeconds = 30
      }
    }));
    services.AddSingleton<IAppHttpClientFactory>(_ =>
      new AppHttpClientFactory(new Uri("https://app.example/"), handlerFactory));
    return services;
  }

  private sealed class TrackingHttpMessageHandler : HttpMessageHandler
  {
    public bool Disposed { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
      Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    protected override void Dispose(bool disposing)
    {
      Disposed = true;
      base.Dispose(disposing);
    }
  }
}
