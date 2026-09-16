using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace MyTransportAppWASM.Services
{
    public class FirebaseAuthenticationStateProvider : AuthenticationStateProvider, IAsyncDisposable
    {
        private readonly IJSRuntime _jsRuntime;
        private DotNetObjectReference<FirebaseAuthenticationStateProvider>? _dotNetRef;
        private ClaimsPrincipal _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        private readonly TaskCompletionSource _initialAuthStateTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _initializationTask;

        public FirebaseAuthenticationStateProvider(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task InitializeAsync()
        {
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
            await _jsRuntime.InvokeVoidAsync("window.firebaseAuthInterop.onAuthStateChanged", _dotNetRef);
        }

        [JSInvokable]
        public void OnAuthStateChanged(FirebaseUser? user)
        {
            SetAuthenticationState(user);
        }

        private void SetAuthenticationState(FirebaseUser? user)
        {
            if (_initialAuthStateTcs.Task.IsCompleted &&
                _currentUser.FindFirst(ClaimTypes.NameIdentifier)?.Value == user?.Uid &&
                (_currentUser.FindFirst(ClaimTypes.Email)?.Value ?? "") == (user?.Email ?? "") &&
                (_currentUser.Identity?.Name ?? "") == (user?.DisplayName ?? "") &&
                (_currentUser.FindFirst("picture")?.Value ?? "") == (user?.PhotoURL ?? ""))
                return;

            if (user != null)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Uid),
                    new Claim(ClaimTypes.Email, user.Email ?? ""),
                    new Claim(ClaimTypes.Name, user.DisplayName ?? ""),
                    new Claim("picture", user.PhotoURL ?? "")
                };

                var identity = new ClaimsIdentity(claims, "Firebase");
                _currentUser = new ClaimsPrincipal(identity);
            }
            else
            {
                _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
            }

            var authState = new AuthenticationState(_currentUser);

            if (!_initialAuthStateTcs.Task.IsCompleted)
            {
                _initialAuthStateTcs.SetResult();
            }

            NotifyAuthenticationStateChanged(Task.FromResult(authState));
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            if (_initialAuthStateTcs.Task.IsCompleted)
                return new AuthenticationState(_currentUser);

            try
            {
                await WaitForInitialStateAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("Firebase authentication did not respond within 10 seconds; continuing anonymously.");
                if (!_initialAuthStateTcs.Task.IsCompleted) SetAuthenticationState(user: null);
            }

            return new AuthenticationState(_currentUser);
        }

        private async Task WaitForInitialStateAsync()
        {
            await InitializeAsync();
            await _initialAuthStateTcs.Task;
        }

        public async Task SignInWithGoogleAsync()
        {
            var user = await _jsRuntime.InvokeAsync<FirebaseUser>("window.firebaseAuthInterop.signInWithGoogle");
            SetAuthenticationState(user);
            if (_initializationTask?.IsFaulted == true) _initializationTask = null;
            await InitializeAsync();
        }

        public async Task SignOutAsync()
        {
            await _jsRuntime.InvokeVoidAsync("window.firebaseAuthInterop.signOut");
            SetAuthenticationState(user: null);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("window.firebaseAuthInterop.disposeAuthStateChanged");
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

    public class FirebaseUser
    {
        public string Uid { get; set; } = "";
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
        public string? PhotoURL { get; set; }
    }
}
