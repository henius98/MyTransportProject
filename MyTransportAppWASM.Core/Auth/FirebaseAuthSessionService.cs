using Microsoft.JSInterop;

namespace MyTransportAppWASM.Core.Auth
{
  public sealed class FirebaseAuthSessionService(FirebaseClientService firebase)
    : IAuthSessionService, IAsyncDisposable
  {
    private DotNetObjectReference<FirebaseAuthSessionService>? _dotNetRef;
    private AuthSessionState _currentState = AuthSessionState.Pending;
    private readonly TaskCompletionSource _initialAuthStateTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initializationTask;

    public AuthSessionState CurrentState => _currentState;
    public event Action<AuthSessionState>? StateChanged;

    public async Task InitializeAsync()
    {
      if (!firebase.IsConfigured)
      {
        if (!_initialAuthStateTcs.Task.IsCompleted) SetAuthenticationState(user: null);
        return;
      }

      _initializationTask ??= InitializeCoreAsync();

      try
      {
        await _initializationTask;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Firebase authentication initialization failed: {ex.Message}");
        if (!_initialAuthStateTcs.Task.IsCompleted) SetAuthenticationState(user: null);
      }
    }

    private async Task InitializeCoreAsync()
    {
      _dotNetRef?.Dispose();
      _dotNetRef = DotNetObjectReference.Create(this);
      await firebase.OnAuthStateChangedAsync(_dotNetRef);
    }

    [JSInvokable]
    public void OnAuthStateChanged(FirebaseUser? user)
    {
      SetAuthenticationState(user);
    }

    private void SetAuthenticationState(FirebaseUser? user)
    {
      var normalizedUser = user is not null && !string.IsNullOrEmpty(user.Uid) ? user : null;
      if (_initialAuthStateTcs.Task.IsCompleted &&
          _currentState.User?.Uid == normalizedUser?.Uid &&
          (_currentState.User?.Email ?? "") == (normalizedUser?.Email ?? "") &&
          (_currentState.User?.DisplayName ?? "") == (normalizedUser?.DisplayName ?? "") &&
          (_currentState.User?.PhotoURL ?? "") == (normalizedUser?.PhotoURL ?? ""))
        return;

      _currentState = normalizedUser is not null
        ? new AuthSessionState(normalizedUser, IsInitialized: true)
        : AuthSessionState.Anonymous;

      _initialAuthStateTcs.TrySetResult();

      StateChanged?.Invoke(_currentState);
    }

    public async Task<AuthSessionState> GetStateAsync()
    {
      if (_initialAuthStateTcs.Task.IsCompleted)
        return _currentState;

      try
      {
        await WaitForInitialStateAsync().WaitAsync(TimeSpan.FromSeconds(10));
      }
      catch (TimeoutException)
      {
        Console.Error.WriteLine("Firebase authentication did not respond within 10 seconds; continuing anonymously.");
        if (!_initialAuthStateTcs.Task.IsCompleted) SetAuthenticationState(user: null);
      }

      return _currentState;
    }

    private async Task WaitForInitialStateAsync()
    {
      await InitializeAsync();
      await _initialAuthStateTcs.Task;
    }

    public async Task SignInWithGoogleAsync(string? clientId = null)
    {
      var user = await firebase.SignInWithGoogleAsync(clientId);
      SetAuthenticationState(user);
      if (_initializationTask?.IsFaulted == true) _initializationTask = null;
      await InitializeAsync();
    }

    public async Task SignOutAsync()
    {
      await firebase.SignOutAsync();
      SetAuthenticationState(user: null);
    }

    public async ValueTask DisposeAsync()
    {
      try
      {
        await firebase.DisposeAuthStateChangedAsync();
      }
      catch (JSException)
      {
        // The module may never have loaded (for example while offline).
      }
      catch (JSDisconnectedException)
      {
        // Normal during browser teardown.
      }

      _dotNetRef?.Dispose();
    }
  }

  public sealed class FirebaseUser
  {
    public string Uid { get; set; } = "";
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? PhotoURL { get; set; }
  }
}
