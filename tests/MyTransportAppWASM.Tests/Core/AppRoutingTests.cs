using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using MyTransportAppWASM.Features;

namespace MyTransportAppWASM.Tests;

public class AppRoutingTests
{
  [Fact]
  public async Task App_RendersOfflineRetryMessageInsteadOfNotFound_WhenLazyAssemblyLoadFails()
  {
    var activator = new AppActivator(
      _ => Task.FromException<IEnumerable<Assembly>>(new HttpRequestException("Offline")));
    var nav = new TestNavigationManager("https://app.example/", "https://app.example/map/?tab=planner");

    await using var provider = CreateAppServices(activator, nav).BuildServiceProvider();
    await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

    var html = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var component = await renderer.RenderComponentAsync<App>();
      return component.ToHtmlString();
    });

    Assert.Contains("Unable to load page", html);
    Assert.Contains("Retry", html);
    Assert.DoesNotContain("Page Not Found", html);
  }

  [Fact]
  public async Task App_RecoversAndRendersRoute_WhenRetrySucceeds()
  {
    var attempts = 0;
    var activator = new AppActivator(_ =>
    {
      if (++attempts == 1)
      {
        return Task.FromException<IEnumerable<Assembly>>(new HttpRequestException("Offline"));
      }

      return Task.FromResult<IEnumerable<Assembly>>([typeof(FeatureComponentBase).Assembly]);
    });
    var nav = new TestNavigationManager("https://app.example/", "https://app.example/fortune");

    var services = CreateAppServices(activator, nav);
    services.AddSingleton(Mock.Of<IBaziFlowService>());

    await using var provider = services.BuildServiceProvider();
    await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

    var html = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var root = await renderer.RenderComponentAsync<App>();
      Assert.Contains("Unable to load page", root.ToHtmlString());

      await InvokeRetryAsync(activator.AppInstance!);
      return root.ToHtmlString();
    });

    Assert.Equal(2, attempts);
    Assert.DoesNotContain("Unable to load page", html);
    Assert.Contains("fortune-page", html);
  }

  [Fact]
  public async Task App_IgnoresStaleRetryFailure_AfterNavigatingToHome()
  {
    var retryGate = new TaskCompletionSource<IEnumerable<Assembly>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var isRetrying = false;
    var attempts = 0;
    var activator = new AppActivator(_ =>
    {
      attempts++;
      if (!isRetrying)
      {
        return Task.FromException<IEnumerable<Assembly>>(new HttpRequestException("Offline"));
      }

      return retryGate.Task;
    });
    var nav = new TestNavigationManager("https://app.example/", "https://app.example/map");

    await using var provider = CreateAppServices(activator, nav).BuildServiceProvider();
    await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

    var html = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var root = await renderer.RenderComponentAsync<App>();
      Assert.Contains("Unable to load page", root.ToHtmlString());

      isRetrying = true;
      var pendingRetry = InvokeRetryAsync(activator.AppInstance!);

      nav.NavigateTo("");
      await Task.Yield();
      Assert.DoesNotContain("Unable to load page", root.ToHtmlString());

      retryGate.SetException(new HttpRequestException("Timed out"));
      await pendingRetry;
      return root.ToHtmlString();
    });

    Assert.Equal(4, attempts);
    Assert.DoesNotContain("Unable to load page", html);
    Assert.DoesNotContain("Page Not Found", html);
  }

  [Fact]
  public async Task App_PreservesLoadedFeaturesAssembly_WhenGtfsFailsAndRetryRecovers()
  {
    var requested = new List<string>();
    var allowGtfs = false;
    var activator = new AppActivator(assemblies =>
    {
      var fileName = assemblies.Single();
      requested.Add(fileName);

      if (fileName == "MyTransportAppWASM.Features.dll")
      {
        return Task.FromResult<IEnumerable<Assembly>>([typeof(FeatureComponentBase).Assembly]);
      }

      if (!allowGtfs)
      {
        return Task.FromException<IEnumerable<Assembly>>(new HttpRequestException("Offline"));
      }

      return Task.FromResult<IEnumerable<Assembly>>([typeof(MyTransportAppWASM.Gtfs.GtfsFeedParser).Assembly]);
    });
    var nav = new TestNavigationManager("https://app.example/", "https://app.example/map");

    var tracking = new Mock<ITransitTrackingService>();
    tracking.Setup(t => t.CachedVehicles).Returns([]);
    var location = new Mock<ILocationService>();
    using var mapSession = new TransitMapSession(
      tracking.Object,
      new Mock<ITransportStaticDataService>().Object,
      location.Object,
      Options.Create(new TransitOptions()),
      Options.Create(new DefaultLocationOptions { Latitude = 3.139, Longitude = 101.6869 }));

    var services = CreateAppServices(activator, nav);
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddSingleton(location.Object);
    services.AddSingleton(mapSession);
    services.AddSingleton(Mock.Of<IPlaceResolutionService>());
    services.AddSingleton(Mock.Of<IBaziFlowService>());

    await using var provider = services.BuildServiceProvider();
    await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

    var html = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var root = await renderer.RenderComponentAsync<App>();
      Assert.Contains("Unable to load page", root.ToHtmlString());
      Assert.DoesNotContain("Page Not Found", root.ToHtmlString());

      // Features already loaded on the partial /map load, so navigating to /fortune works without reloading Features.
      nav.NavigateTo("fortune");
      await Task.Yield();
      Assert.Contains("fortune-page", root.ToHtmlString());

      // Navigating back to /map and retrying loads only the previously failed GTFS assembly.
      nav.NavigateTo("map");
      await Task.Yield();
      Assert.Contains("Unable to load page", root.ToHtmlString());

      allowGtfs = true;
      await InvokeRetryAsync(activator.AppInstance!);
      return root.ToHtmlString();
    });

    Assert.Equal(1, requested.Count(name => name == "MyTransportAppWASM.Features.dll"));
    Assert.Equal(3, requested.Count(name => name == "MyTransportAppWASM.Gtfs.dll"));
    Assert.DoesNotContain("Unable to load page", html);
    Assert.DoesNotContain("Page Not Found", html);
    Assert.Contains("map-container", html);
  }

  [Fact]
  public async Task App_RecoversFeaturesAssemblyFromAppDomain_WhenLazyLoaderReturnsEmpty()
  {
    var activator = new AppActivator(_ => Task.FromResult<IEnumerable<Assembly>>([]));
    var nav = new TestNavigationManager("https://app.example/", "https://app.example/fortune");

    var services = CreateAppServices(activator, nav);
    services.AddSingleton(Mock.Of<IBaziFlowService>());

    await using var provider = services.BuildServiceProvider();
    await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

    var html = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var root = await renderer.RenderComponentAsync<App>();
      return root.ToHtmlString();
    });

    Assert.DoesNotContain("Page Not Found", html);
    Assert.Contains("fortune-page", html);
  }

  [Fact]
  public async Task App_RendersOfflineRetryMessage_WhenFeaturesAssemblyResolvesToNull()
  {
    var activator = new AppActivator(
      _ => Task.FromResult<IEnumerable<Assembly>>([]),
      _ => null);
    var nav = new TestNavigationManager("https://app.example/", "https://app.example/fortune");

    await using var provider = CreateAppServices(activator, nav).BuildServiceProvider();
    await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

    var html = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var root = await renderer.RenderComponentAsync<App>();
      return root.ToHtmlString();
    });

    Assert.Contains("Unable to load page", html);
    Assert.Contains("Retry", html);
    Assert.DoesNotContain("Page Not Found", html);
  }

  private static Task InvokeRetryAsync(App app)
  {
    var retryMethod = typeof(App).GetMethod("RetryRouteLoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var retryCallback = (Func<Task>)Delegate.CreateDelegate(typeof(Func<Task>), app, retryMethod);
    return ((IHandleEvent)app).HandleEventAsync(new EventCallbackWorkItem(retryCallback), null);
  }

  private static ServiceCollection CreateAppServices(AppActivator activator, TestNavigationManager nav)
  {
    var jsRuntime = new Mock<IJSRuntime>();
    var language = new LanguageService(new HttpClient());
    language.LoadDefaultLanguage();

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(jsRuntime.Object);
    services.AddSingleton<IComponentActivator>(activator);
    services.AddSingleton<NavigationManager>(nav);
    services.AddSingleton(Mock.Of<INavigationInterception>());
    services.AddSingleton(Mock.Of<IScrollToLocationHash>());
    services.AddSingleton(Mock.Of<IErrorBoundaryLogger>());
    services.AddScoped<LazyAssemblyLoader>();
    services.AddScoped<IBrowserStorageService>(_ => Mock.Of<IBrowserStorageService>());
    services.AddScoped<IUserSettingsService>(_ => Mock.Of<IUserSettingsService>());
    services.AddScoped<AppStateService>();
    services.AddSingleton(Options.Create(new FirebaseOptions()));
    services.AddScoped<FirebaseClientService>();
    services.AddScoped<IAuthSessionService>(_ => new StubAuthSessionService());
    services.AddCascadingValue(_ => AuthSessionState.Anonymous);
    services.AddScoped(_ => new ThemeService(jsRuntime.Object, Mock.Of<IUserSettingsService>()));
    services.AddScoped(_ => language);
    services.AddCascadingValue(sp => sp.GetRequiredService<ThemeService>());
    services.AddCascadingValue(sp => sp.GetRequiredService<LanguageService>());
    return services;
  }

  private sealed class TestNavigationManager : NavigationManager
  {
    public TestNavigationManager(string baseUri, string uri)
    {
      Initialize(baseUri, uri);
    }

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
      Uri = ToAbsoluteUri(uri).ToString();
      NotifyLocationChanged(isInterceptedLink: false);
    }
  }

  private sealed class AppActivator(
    Func<IEnumerable<string>, Task<IEnumerable<Assembly>>> loader,
    Func<string, Assembly?>? findLoaded = null) : IComponentActivator
  {
    public App? AppInstance { get; private set; }

    public IComponent CreateInstance(Type componentType)
    {
      var instance = (IComponent)Activator.CreateInstance(componentType)!;
      if (instance is App app)
      {
        app.LoadAssembliesOverride = loader;
        app.FindLoadedAssemblyOverride = findLoaded;
        AppInstance = app;
      }
      return instance;
    }
  }

  private sealed class StubAuthSessionService : IAuthSessionService
  {
    public AuthSessionState CurrentState => AuthSessionState.Anonymous;
    public event Action<AuthSessionState>? StateChanged { add { } remove { } }
    public Task<AuthSessionState> GetStateAsync() => Task.FromResult(AuthSessionState.Anonymous);
    public Task SignInWithGoogleAsync(string? clientId = null) => Task.CompletedTask;
    public Task SignOutAsync() => Task.CompletedTask;
  }
}
