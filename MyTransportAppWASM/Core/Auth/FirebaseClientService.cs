using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace MyTransportAppWASM.Core.Auth;

public sealed class FirebaseClientService : IAsyncDisposable
{
  private const string AuthModulePath = "./js/firebase-auth.js";
  private const string FirestoreModulePath = "./js/firebase-firestore.js";
  private const string PushModulePath = "./js/push-notifications.js";

  private readonly IJSRuntime _jsRuntime;
  private readonly FirebaseOptions _options;
  private readonly SemaphoreSlim _configLock = new(1, 1);
  private readonly JsModuleLoader _authModule;
  private readonly JsModuleLoader _firestoreModule;
  private readonly JsModuleLoader _pushModule;

  private Task? _configureTask;
  private bool _disposed;

  public bool IsConfigured =>
      IsValidConfigValue(_options.ApiKey)
      && IsValidConfigValue(_options.AuthDomain)
      && IsValidConfigValue(_options.ProjectId)
      && IsValidConfigValue(_options.AppId);

  private static bool IsValidConfigValue(string? value) =>
      !string.IsNullOrWhiteSpace(value) && !value.StartsWith("YOUR_", StringComparison.Ordinal);

  public FirebaseClientService(IJSRuntime jsRuntime, IOptions<FirebaseOptions>? options = null)
  {
    _jsRuntime = jsRuntime;
    _options = options?.Value ?? new FirebaseOptions();
    _authModule = new JsModuleLoader(jsRuntime, AuthModulePath, EnsureConfiguredAsync);
    _firestoreModule = new JsModuleLoader(jsRuntime, FirestoreModulePath, EnsureConfiguredAsync);
    _pushModule = new JsModuleLoader(jsRuntime, PushModulePath, EnsureConfiguredAsync);
  }

  public Task EnsureConfiguredAsync()
  {
    if (_configureTask is { IsCompletedSuccessfully: true })
      return _configureTask;

    return EnsureConfiguredCoreAsync();
  }

  private async Task EnsureConfiguredCoreAsync()
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    await _configLock.WaitAsync();
    try
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_configureTask is null || _configureTask.IsFaulted || _configureTask.IsCanceled)
        _configureTask = ConfigureCoreAsync();

      await _configureTask;
    }
    finally
    {
      _configLock.Release();
    }
  }

  private async Task ConfigureCoreAsync()
  {
    await using var configModule = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/firebase-config.js");
    await configModule.InvokeVoidAsync("configureFirebase", _options);
  }

  public async Task OnAuthStateChangedAsync(DotNetObjectReference<FirebaseAuthenticationStateProvider> dotNetHelper)
  {
    var module = await GetAuthModuleAsync();
    await module.InvokeVoidAsync("onAuthStateChanged", dotNetHelper);
  }

  public async Task<FirebaseUser> SignInWithGoogleAsync(string? clientId = null)
  {
    var module = await GetAuthModuleAsync();
    var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? _options.GoogleClientId : clientId;
    return string.IsNullOrWhiteSpace(effectiveClientId)
        ? await module.InvokeAsync<FirebaseUser>("signInWithGoogle")
        : await module.InvokeAsync<FirebaseUser>("signInWithGoogle", effectiveClientId);
  }

  public async Task SignOutAsync()
  {
    var module = await GetAuthModuleAsync();
    await module.InvokeVoidAsync("signOut");
  }

  public async Task DisposeAuthStateChangedAsync()
  {
    if (_authModule.LoadedModule is not { } module) return;
    await module.InvokeVoidAsync("disposeAuthStateChanged");
  }

  public async Task InitializeOneTapAsync(string? clientId = null)
  {
    if (!IsConfigured) return;
    var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? _options.GoogleClientId : clientId;
    if (string.IsNullOrWhiteSpace(effectiveClientId)) return;
    var module = await GetAuthModuleAsync();
    await module.InvokeVoidAsync("initializeOneTap", effectiveClientId);
  }

  public async Task CancelOneTapAsync()
  {
    if (await _authModule.GetIfStartedAsync() is not { } module) return;
    await module.InvokeVoidAsync("cancelOneTap");
  }

  public async Task<UserSettings?> GetUserSettingsAsync(string uid, TimeSpan timeout)
  {
    var module = await GetFirestoreModuleAsync();
    return await module.InvokeAsync<UserSettings?>("getUserSettings", timeout, uid);
  }

  public async Task SetUserSettingsAsync(string uid, object settings, TimeSpan timeout)
  {
    var module = await GetFirestoreModuleAsync();
    await module.InvokeVoidAsync("setUserSettings", timeout, uid, settings);
  }

  public async Task<string> GetPushStatusAsync(string? uid)
  {
    var module = await GetPushModuleAsync();
    return await module.InvokeAsync<string>("status", uid);
  }

  public async Task EnablePushAsync(string uid)
  {
    var module = await GetPushModuleAsync();
    await module.InvokeVoidAsync("enable", uid);
  }

  public async Task DisablePushAsync(string uid)
  {
    var module = await GetPushModuleAsync();
    await module.InvokeVoidAsync("disable", uid);
  }

  public async Task EnsurePushForegroundListenerAsync()
  {
    var module = await GetPushModuleAsync();
    await module.InvokeVoidAsync("ensureForegroundListener");
  }

  private Task<IJSObjectReference> GetAuthModuleAsync() => _authModule.GetAsync();

  private Task<IJSObjectReference> GetFirestoreModuleAsync() => _firestoreModule.GetAsync();

  private Task<IJSObjectReference> GetPushModuleAsync() => _pushModule.GetAsync();

  public async ValueTask DisposeAsync()
  {
    if (_disposed) return;
    _disposed = true;

    await _authModule.DisposeAsync();
    await _firestoreModule.DisposeAsync();
    await _pushModule.DisposeAsync();
    _configLock.Dispose();
  }
}
