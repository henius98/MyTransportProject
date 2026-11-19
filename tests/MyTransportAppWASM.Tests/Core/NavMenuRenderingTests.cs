using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;

namespace MyTransportAppWASM.Tests;

public class NavMenuRenderingTests
{
  [Theory]
  [InlineData("disabled", "enable", "EnableNotificationsFailed", "Could not enable notifications. Please try again.")]
  [InlineData("enabled", "disable", "DisableNotificationsFailed", "Could not disable notifications. Please try again.")]
  public async Task PushToggleFailureShowsSimpleLocalizedMessageWithoutStackTrace(
      string initialStatus,
      string jsAction,
      string translationKey,
      string expectedMessage)
  {
    var rawJsError = """
            Failed to execute 'subscribe' on 'PushManager': Subscription failed - no active Service Worker
            AbortError: Failed to execute 'subscribe' on 'PushManager': Subscription failed - no active Service Worker
                at getPushSubscription (https://www.gstatic.com/firebasejs/11.4.0/firebase-messaging.js:1:20061)
                at async Module.enable (https://example.com/js/push-notifications.js:139:19)
            """;

    var configModule = new Mock<IJSObjectReference>();
    configModule
        .Setup(m => m.InvokeAsync<IJSVoidResult>("configureFirebase", It.IsAny<object?[]?>()))
        .Returns(ValueTask.FromResult(Mock.Of<IJSVoidResult>()));

    var pushModule = new Mock<IJSObjectReference>();
    pushModule
        .Setup(m => m.InvokeAsync<string>("status", It.IsAny<object?[]?>()))
        .ReturnsAsync(initialStatus);
    pushModule
        .Setup(m => m.InvokeAsync<IJSVoidResult>(jsAction, It.IsAny<object?[]?>()))
        .ThrowsAsync(new JSException(rawJsError));

    var js = new Mock<IJSRuntime>();
    js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.Is<object?[]?>(args => args != null && Equals(args[0], "./js/firebase-config.js"))))
        .ReturnsAsync(configModule.Object);
    js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.Is<object?[]?>(args => args != null && Equals(args[0], "./js/push-notifications.js"))))
        .ReturnsAsync(pushModule.Object);

    using var httpClient = new HttpClient(new TranslationHandler(translationKey, expectedMessage))
    {
      BaseAddress = new Uri("https://app.example/")
    };
    var language = new LanguageService(httpClient);
    await language.LoadLanguageAsync("en-US");

    var userSettings = new Mock<IUserSettingsService>();
    var authState = new AuthSessionState(
        new FirebaseUser { Uid = "user-123", DisplayName = "Henius" },
        IsInitialized: true);
    var authSession = new TestAuthSessionService(authState);

    var activator = new NavMenuActivator();
    var services = new ServiceCollection()
        .AddLogging()
        .AddSingleton<NavigationManager>(new TestNavigationManager())
        .AddSingleton(js.Object)
        .AddSingleton(new FirebaseClientService(js.Object))
        .AddSingleton(new AppStateService())
        .AddSingleton<IAuthSessionService>(authSession)
        .AddSingleton(userSettings.Object)
        .AddSingleton<IComponentActivator>(activator)
        .AddCascadingValue(_ => language)
        .AddCascadingValue(_ => new ThemeService(js.Object, userSettings.Object))
        .AddCascadingValue(_ => authState)
        .BuildServiceProvider();

    await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

    var html = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var root = await renderer.RenderComponentAsync<NavMenu>();
      var menu = activator.Component!;

      var toggleUserMenu = typeof(NavMenu).GetMethod("ToggleUserMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
      await (Task)toggleUserMenu.Invoke(menu, null)!;

      var togglePush = typeof(NavMenu).GetMethod("TogglePushNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!;
      await (Task)togglePush.Invoke(menu, null)!;

      var stateHasChanged = typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
      stateHasChanged.Invoke(menu, null);

      return root.ToHtmlString();
    });

    Assert.Contains(expectedMessage, html);
    Assert.DoesNotContain("PushManager", html);
    Assert.DoesNotContain("AbortError", html);
    Assert.DoesNotContain("getPushSubscription", html);

    var reopenedHtml = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var root = await renderer.RenderComponentAsync<NavMenu>();
      var menu = activator.Component!;

      var toggleUserMenu = typeof(NavMenu).GetMethod("ToggleUserMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
      await (Task)toggleUserMenu.Invoke(menu, null)!;

      var togglePush = typeof(NavMenu).GetMethod("TogglePushNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!;
      await (Task)togglePush.Invoke(menu, null)!;

      // Close and reopen menu; stale pushError should be cleared.
      await (Task)toggleUserMenu.Invoke(menu, null)!;
      await (Task)toggleUserMenu.Invoke(menu, null)!;

      var stateHasChanged = typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
      stateHasChanged.Invoke(menu, null);

      return root.ToHtmlString();
    });

    Assert.DoesNotContain(expectedMessage, reopenedHtml);
  }

  [Fact]
  public async Task PushEnableFailureWhenPermissionDeniedUpdatesStatusAndShowsDeniedMessage()
  {
    var configModule = new Mock<IJSObjectReference>();
    configModule
        .Setup(m => m.InvokeAsync<IJSVoidResult>("configureFirebase", It.IsAny<object?[]?>()))
        .Returns(ValueTask.FromResult(Mock.Of<IJSVoidResult>()));

    var pushModule = new Mock<IJSObjectReference>();
    pushModule
        .SetupSequence(m => m.InvokeAsync<string>("status", It.IsAny<object?[]?>()))
        .ReturnsAsync("disabled")
        .ReturnsAsync("denied");
    pushModule
        .Setup(m => m.InvokeAsync<IJSVoidResult>("enable", It.IsAny<object?[]?>()))
        .ThrowsAsync(new JSException("Allow notifications in your browser settings, then try again."));

    var js = new Mock<IJSRuntime>();
    js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.Is<object?[]?>(args => args != null && Equals(args[0], "./js/firebase-config.js"))))
        .ReturnsAsync(configModule.Object);
    js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.Is<object?[]?>(args => args != null && Equals(args[0], "./js/push-notifications.js"))))
        .ReturnsAsync(pushModule.Object);

    const string deniedMessage = "Allow notifications for this site in your browser settings.";
    using var httpClient = new HttpClient(new TranslationHandler("NotificationsDenied", deniedMessage))
    {
      BaseAddress = new Uri("https://app.example/")
    };
    var language = new LanguageService(httpClient);
    await language.LoadLanguageAsync("en-US");

    var userSettings = new Mock<IUserSettingsService>();
    var authState = new AuthSessionState(
        new FirebaseUser { Uid = "user-123", DisplayName = "Henius" },
        IsInitialized: true);
    var activator = new NavMenuActivator();
    var services = new ServiceCollection()
        .AddLogging()
        .AddSingleton<NavigationManager>(new TestNavigationManager())
        .AddSingleton(js.Object)
        .AddSingleton(new FirebaseClientService(js.Object))
        .AddSingleton(new AppStateService())
        .AddSingleton<IAuthSessionService>(new TestAuthSessionService(authState))
        .AddSingleton(userSettings.Object)
        .AddSingleton<IComponentActivator>(activator)
        .AddCascadingValue(_ => language)
        .AddCascadingValue(_ => new ThemeService(js.Object, userSettings.Object))
        .AddCascadingValue(_ => authState)
        .BuildServiceProvider();

    await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

    var html = await renderer.Dispatcher.InvokeAsync(async () =>
    {
      var root = await renderer.RenderComponentAsync<NavMenu>();
      var menu = activator.Component!;

      var toggleUserMenu = typeof(NavMenu).GetMethod("ToggleUserMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
      await (Task)toggleUserMenu.Invoke(menu, null)!;

      var togglePush = typeof(NavMenu).GetMethod("TogglePushNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!;
      await (Task)togglePush.Invoke(menu, null)!;

      var stateHasChanged = typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
      stateHasChanged.Invoke(menu, null);

      return root.ToHtmlString();
    });

    Assert.Contains(deniedMessage, html);
  }

  private sealed class NavMenuActivator : IComponentActivator
  {
    public NavMenu? Component { get; private set; }

    public IComponent CreateInstance(Type componentType)
    {
      var instance = (IComponent)Activator.CreateInstance(componentType)!;
      if (instance is NavMenu menu)
        Component = menu;
      return instance;
    }
  }

  private sealed class TestAuthSessionService(AuthSessionState state) : IAuthSessionService
  {
    public AuthSessionState CurrentState => state;
    public event Action<AuthSessionState>? StateChanged { add { } remove { } }
    public Task<AuthSessionState> GetStateAsync() => Task.FromResult(state);
    public Task SignInWithGoogleAsync(string? clientId = null) => Task.CompletedTask;
    public Task SignOutAsync() => Task.CompletedTask;
  }

  private sealed class TestNavigationManager : NavigationManager
  {
    public TestNavigationManager() => Initialize("https://app.example/", "https://app.example/");
    protected override void NavigateToCore(string uri, bool forceLoad) { }
  }

  private sealed class TranslationHandler(string key, string value) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent($$"""{"{{key}}":"{{value}}"}""")
        });
  }
}
