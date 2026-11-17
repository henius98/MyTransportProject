using Microsoft.JSInterop;

namespace MyTransportAppWASM.Tests;

public class UserSettingsServiceTests
{
    [Fact]
    public async Task LoginLoadsCloudPreferences_AndLogoutRestoresGuest()
    {
        await using var h = new SettingsHarness(uid: null, cloud: [("alice", new() { Theme = "light", Language = "ms-MY" })]);
        await h.Settings.UpdateThemeAsync("dark");
        await h.Settings.UpdateLanguageAsync("zh-CN");

        h.SetUser("alice");
        Assert.Equal("ms-MY", (await h.Settings.GetSettingsAsync()).Language);
        await h.Settings.UpdateThemeAsync("dark");
        Assert.Equal("dark", h.Js.Cloud["alice"].Theme);
        Assert.Equal("ms-MY", h.Js.Cloud["alice"].Language);

        h.SetUser(null);
        Assert.Equal("zh-CN", (await h.Settings.GetSettingsAsync()).Language);
        Assert.Equal("dark", (await h.Settings.GetSettingsAsync()).Theme);
    }

    [Fact]
    public async Task NewAccountDoesNotInheritAnotherAccountsSettings_OrWriteOnRead()
    {
        await using var h = new SettingsHarness(cloud: [("alice", new() { Language = "ms-MY", HasSeenWelcome = true })]);
        await h.Settings.GetSettingsAsync();
        h.SetUser("bob");

        var bob = await h.Settings.GetSettingsAsync();
        Assert.Null(bob.Language);
        Assert.Null(bob.HasSeenWelcome);
        Assert.Empty(h.Js.Writes);
        await h.Settings.UpdateLanguageAsync("en-US");
        Assert.Equal("ms-MY", h.Js.Cloud["alice"].Language);
        Assert.Equal("en-US", h.Js.Cloud["bob"].Language);
    }

    [Fact]
    public async Task FailedCloudReadUsesOnlyThatAccountsCache_AndNeverOverwritesCloud()
    {
        await using var h = new SettingsHarness(cloud: [("alice", new() { Language = "ms-MY" })]);
        await h.Settings.GetSettingsAsync();

        h.SetUser(null);
        h.Js.FailReads = true;
        h.SetUser("bob");
        Assert.Null((await h.Settings.GetSettingsAsync()).Language);
        Assert.NotNull(h.Settings.SyncError);

        h.SetUser("alice");
        Assert.Equal("ms-MY", (await h.Settings.GetSettingsAsync()).Language);
        Assert.Empty(h.Js.Writes);
    }

    [Fact]
    public async Task FailedWriteKeepsLocalPreferencesAndReportsSyncFailure()
    {
        await using var h = new SettingsHarness(failWrites: true);
        await h.Settings.UpdateThemeAsync("light");
        Assert.NotNull(h.Settings.SyncError);
        Assert.Contains("light", h.Js.Storage["preferences:user:alice"]);

        h.Js.FailWrites = false;
        await h.Settings.UpdateThemeAsync("light");
        await h.Settings.UpdateLanguageAsync("ms-MY");
        Assert.Null(h.Settings.SyncError);
        Assert.Equal("light", h.Js.Cloud["alice"].Theme);
    }

    [Fact]
    public async Task AccountSwitchDuringCloudReadDiscardsOldResult()
    {
        await using var h = new SettingsHarness(cloud: [("alice", new() { Language = "ms-MY" }), ("bob", new() { Language = "en-US" })]);
        var (started, resume) = h.PauseRead("alice");

        var pending = h.Settings.GetSettingsAsync();
        await started.Task;
        h.SetUser("bob");
        resume.SetResult();

        Assert.Equal("en-US", (await pending).Language);
        Assert.Equal("en-US", (await h.Settings.GetSettingsAsync()).Language);
        Assert.Empty(h.Js.Writes);
    }

    [Fact]
    public async Task AccountSwitchDuringPreferenceUpdateDoesNotWriteToNewAccount()
    {
        await using var h = new SettingsHarness(cloud: [("alice", new() { Theme = "dark" }), ("bob", new() { Theme = "dark" })]);
        var (started, resume) = h.PauseRead("alice");

        var pending = h.Settings.UpdateThemeAsync("light");
        await started.Task;
        h.SetUser("bob");
        resume.SetResult();
        await pending;

        Assert.Empty(h.Js.Writes);
        Assert.Equal("dark", h.Js.Cloud["bob"].Theme);
    }

    [Fact]
    public async Task EditingAfterFailedReadPreservesUnrelatedCloudFields()
    {
        await using var h = new SettingsHarness(failReads: true, cloud: [("alice", new() { Language = "ms-MY", HasSeenWelcome = true })]);

        await h.Settings.UpdateThemeAsync("light");

        Assert.Equal("light", h.Js.Cloud["alice"].Theme);
        Assert.Equal("ms-MY", h.Js.Cloud["alice"].Language);
        Assert.True(h.Js.Cloud["alice"].HasSeenWelcome);
    }

    [Fact]
    public async Task ConcurrentPreferenceChangesPreserveBothFields()
    {
        await using var h = new SettingsHarness();
        await Task.WhenAll(h.Settings.UpdateThemeAsync("light"), h.Settings.UpdateLanguageAsync("zh-CN"));
        Assert.Equal("light", h.Js.Cloud["alice"].Theme);
        Assert.Equal("zh-CN", h.Js.Cloud["alice"].Language);
    }

    [Fact]
    public async Task DisposeDuringAuthStateChange_DoesNotThrowObjectDisposedException()
    {
        await using var h = new SettingsHarness(startUser: false);
        var (enteredRead, releaseRead) = h.PauseRead();

        h.SetUser("alice");
        var readTask = h.Settings.GetSettingsAsync();
        await enteredRead.Task;

        h.SetUser(null);
        h.Settings.Dispose();
        releaseRead.TrySetResult();

        await readTask;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Settings.GetSettingsAsync());
    }

    private sealed class SettingsHarness : IAsyncDisposable
    {
        private readonly FirebaseClientService _firebase;
        private readonly FirebaseAuthenticationStateProvider _auth;
        public SettingsJsRuntime Js { get; }
        public UserSettingsService Settings { get; }

        public SettingsHarness(
            string? uid = "alice",
            bool failReads = false,
            bool failWrites = false,
            bool startUser = true,
            (string Uid, UserSettings Value)[]? cloud = null)
        {
            Js = new SettingsJsRuntime { FailReads = failReads, FailWrites = failWrites };
            foreach (var (k, v) in cloud ?? []) Js.Cloud[k] = v;
            _firebase = new FirebaseClientService(Js);
            _auth = new FirebaseAuthenticationStateProvider(_firebase);
            if (startUser) SetUser(uid);
            Settings = new UserSettingsService(new BrowserStorageService(Js), _auth, _firebase);
        }

        public void SetUser(string? uid) =>
            _auth.OnAuthStateChanged(uid is null ? null : new FirebaseUser { Uid = uid });

        public (TaskCompletionSource Started, TaskCompletionSource Resume) PauseRead(string? targetUid = null)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Js.BeforeRead = async uid =>
            {
                if (targetUid is not null && uid != targetUid) return;
                started.TrySetResult();
                await resume.Task;
            };
            return (started, resume);
        }

        public async ValueTask DisposeAsync()
        {
            Settings.Dispose();
            await _auth.DisposeAsync();
            await _firebase.DisposeAsync();
        }
    }

    private sealed class SettingsJsRuntime : IJSRuntime
    {
        public Dictionary<string, string> Storage { get; } = new();
        public Dictionary<string, UserSettings> Cloud { get; } = new();
        public List<string> Writes { get; } = new();
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public Func<string, Task>? BeforeRead { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                var modulePath = Assert.IsType<string>(args![0]);
                return ValueTask.FromResult((TValue)(object)new SettingsModule(this, modulePath));
            }

            var key = Assert.IsType<string>(args![0]);
            object? result = null;
            switch (identifier)
            {
                case "localStorage.getItem":
                    Storage.TryGetValue(key, out var value);
                    result = value;
                    break;
                case "localStorage.setItem":
                    Storage[key] = Assert.IsType<string>(args[1]);
                    break;
                default:
                    throw new InvalidOperationException(identifier);
            }
            return ValueTask.FromResult((TValue)result!);
        }

        private sealed class SettingsModule(SettingsJsRuntime owner, string modulePath) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                InvokeAsync<TValue>(identifier, CancellationToken.None, args);

            public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            {
                if (modulePath is "./js/firebase-config.js" or "./js/firebase-auth.js")
                    return default!;

                Assert.Equal("./js/firebase-firestore.js", modulePath);
                var key = Assert.IsType<string>(args![0]);
                object? result = null;
                switch (identifier)
                {
                    case "getUserSettings":
                        if (owner.BeforeRead != null) await owner.BeforeRead(key);
                        if (owner.FailReads) throw new JSException("Offline");
                        if (owner.Cloud.TryGetValue(key, out var settings)) result = settings with { };
                        break;
                    case "setUserSettings":
                        if (owner.FailWrites) throw new JSException("Permission denied");
                        owner.Writes.Add(key);
                        if (args[1] is UserSettings fullSettings)
                        {
                            owner.Cloud[key] = fullSettings with { };
                        }
                        else
                        {
                            var patch = Assert.IsType<Dictionary<string, object>>(args[1]);
                            var saved = owner.Cloud.GetValueOrDefault(key) ?? new UserSettings();
                            foreach (var (field, fieldValue) in patch)
                            {
                                saved = field switch
                                {
                                    "theme" => saved with { Theme = (string)fieldValue },
                                    "language" => saved with { Language = (string)fieldValue },
                                    "hasSeenWelcome" => saved with { HasSeenWelcome = (bool)fieldValue },
                                    "defaultLocation" => saved with { DefaultLocation = (UserLocation)fieldValue },
                                    _ => throw new InvalidOperationException(field)
                                };
                            }
                            owner.Cloud[key] = saved;
                        }
                        break;
                    default:
                        throw new InvalidOperationException(identifier);
                }
                return (TValue)result!;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
