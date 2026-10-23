using Microsoft.JSInterop;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class FirebaseAuthenticationStateProviderTests
{
    [Fact]
    public async Task GetAuthenticationStateAsync_ContinuesAnonymously_WhenInteropInitializationFails()
    {
        var provider = new FirebaseAuthenticationStateProvider(new StubJsRuntime(shouldFail: true));

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task OnAuthStateChanged_CreatesExpectedClaims()
    {
        var provider = new FirebaseAuthenticationStateProvider(new StubJsRuntime(shouldFail: false));

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
        await using var provider = new FirebaseAuthenticationStateProvider(new StubJsRuntime(false));
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
        await using var provider = new FirebaseAuthenticationStateProvider(new StubJsRuntime(false));
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

    private sealed class StubJsRuntime(bool shouldFail) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            return shouldFail
                ? ValueTask.FromException<TValue>(new JSException("Interop unavailable"))
                : ValueTask.FromResult(default(TValue)!);
        }
    }
}
