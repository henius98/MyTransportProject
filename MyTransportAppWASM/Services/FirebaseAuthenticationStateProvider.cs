using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace MyTransportAppWASM.Services
{
    public class FirebaseAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
    {
        private readonly IJSRuntime _jsRuntime;
        private DotNetObjectReference<FirebaseAuthenticationStateProvider>? _dotNetRef;
        private ClaimsPrincipal _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        private TaskCompletionSource<AuthenticationState> _initialAuthStateTcs = new TaskCompletionSource<AuthenticationState>();
        private bool _isInitialized;

        public FirebaseAuthenticationStateProvider(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task InitializeAsync()
        {
            if (!_isInitialized)
            {
                _isInitialized = true;
                _dotNetRef = DotNetObjectReference.Create(this);
                await _jsRuntime.InvokeVoidAsync("window.firebaseAuthInterop.onAuthStateChanged", _dotNetRef);
            }
        }

        [JSInvokable]
        public void OnAuthStateChanged(FirebaseUser? user)
        {
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
                _initialAuthStateTcs.SetResult(authState);
            }

            NotifyAuthenticationStateChanged(Task.FromResult(authState));
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            await InitializeAsync();
            return await _initialAuthStateTcs.Task;
        }

        public async Task SignInWithGoogleAsync()
        {
            await _jsRuntime.InvokeVoidAsync("window.firebaseAuthInterop.signInWithGoogle");
        }

        public async Task SignOutAsync()
        {
            await _jsRuntime.InvokeVoidAsync("window.firebaseAuthInterop.signOut");
        }

        public void Dispose()
        {
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
