using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace MyTransportAppWASM.Tests;

public class FirebaseAuthenticationStateProviderTests
{
    [Fact]
    public async Task GetAuthenticationStateAsync_ContinuesAnonymously_WhenInteropInitializationFails()
    {
        var firebase = new FirebaseClientService(new StubJsRuntime(shouldFail: true));
        var provider = new FirebaseAuthenticationStateProvider(firebase);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task OnAuthStateChanged_CreatesExpectedClaims()
    {
        var firebase = new FirebaseClientService(new StubJsRuntime(shouldFail: false));
        var provider = new FirebaseAuthenticationStateProvider(firebase);

        provider.OnAuthStateChanged(new FirebaseUser
        {
            Uid = "user-123",
            Email = "rider@example.test",
            DisplayName = "Test Rider",
            PhotoURL = "https://example.test/avatar.png"
        });
        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal("user-123", state.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("rider@example.test", state.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value);
        Assert.Equal("Test Rider", state.User.Identity?.Name);
        Assert.Equal("https://example.test/avatar.png", state.User.FindFirst("picture")?.Value);
    }

    [Fact]
    public async Task ReadsFollowLoginAccountSwitchAndLogout_AfterAnonymousStartup()
    {
        await using var firebase = new FirebaseClientService(new StubJsRuntime(false));
        await using var provider = new FirebaseAuthenticationStateProvider(firebase);
        provider.OnAuthStateChanged(null);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);

        provider.OnAuthStateChanged(new FirebaseUser { Uid = "alice", DisplayName = "Alice" });
        Assert.Equal("Alice", (await provider.GetAuthenticationStateAsync()).User.Identity!.Name);

        provider.OnAuthStateChanged(new FirebaseUser { Uid = "bob", DisplayName = "Bob" });
        Assert.Equal("Bob", (await provider.GetAuthenticationStateAsync()).User.Identity!.Name);

        provider.OnAuthStateChanged(null);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
    }

    [Fact]
    public async Task LogoutDoesNotReturnRestoredSession_AndDuplicateCallbacksDoNotNotify()
    {
        await using var firebase = new FirebaseClientService(new StubJsRuntime(false));
        await using var provider = new FirebaseAuthenticationStateProvider(firebase);
        provider.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);

        var notifications = 0;
        provider.AuthenticationStateChanged += _ => notifications++;
        provider.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        await provider.SignOutAsync();
        provider.OnAuthStateChanged(null);

        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task SignInWithGoogleAsync_PushesConfigFirst_AndPassesClientId_WhenProvided()
    {
        var stub = new StubJsRuntime(false);
        var options = Options.Create(new FirebaseOptions { ApiKey = "k", GoogleClientId = "default-client" });
        await using var firebase = new FirebaseClientService(stub, options);
        await using var provider = new FirebaseAuthenticationStateProvider(firebase);

        await provider.SignInWithGoogleAsync("client-123.apps.googleusercontent.com");

        Assert.Contains(stub.Invocations, i => i.Module == "./js/firebase-config.js" && i.Identifier == "configureFirebase");
        var signInCall = stub.Invocations.FirstOrDefault(i => i.Module == "./js/firebase-auth.js" && i.Identifier == "signInWithGoogle");
        Assert.NotNull(signInCall.Identifier);
        Assert.NotNull(signInCall.Args);
        Assert.Single(signInCall.Args!);
        Assert.Equal("client-123.apps.googleusercontent.com", signInCall.Args![0]);
    }

    [Fact]
    public async Task SignInWithGoogleAsync_UsesConfiguredClientIdOrOmitsArgs_WhenClientIdIsNullOrWhitespace()
    {
        var stub = new StubJsRuntime(false);
        await using var unconfiguredFirebase = new FirebaseClientService(stub);
        await using var provider = new FirebaseAuthenticationStateProvider(unconfiguredFirebase);

        await provider.SignInWithGoogleAsync(null);

        var signInCall = stub.Invocations.FirstOrDefault(i => i.Module == "./js/firebase-auth.js" && i.Identifier == "signInWithGoogle");
        Assert.NotNull(signInCall.Identifier);
        Assert.Empty(signInCall.Args!);
    }

    private sealed class StubJsRuntime(bool shouldFail) : IJSRuntime
    {
        public List<(string Module, string Identifier, object?[]? Args)> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (shouldFail)
                return ValueTask.FromException<TValue>(new JSException("Interop unavailable"));

            if (identifier == "import")
            {
                var path = Assert.IsType<string>(args![0]);
                return ValueTask.FromResult((TValue)(object)new StubJsModule(path, Invocations));
            }

            Invocations.Add(("", identifier, args));
            return ValueTask.FromResult(default(TValue)!);
        }

        private sealed class StubJsModule(string path, List<(string Module, string Identifier, object?[]? Args)> invocations) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                InvokeAsync<TValue>(identifier, CancellationToken.None, args);

            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            {
                invocations.Add((path, identifier, args));
                return ValueTask.FromResult(default(TValue)!);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
