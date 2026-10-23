using Microsoft.JSInterop;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class UserSettingsServiceTests
{
    [Fact]
    public async Task LoginLoadsCloudPreferences_AndLogoutRestoresGuest()
    {
        var js = new SettingsJsRuntime();
        js.Cloud["alice"] = new UserSettings { Theme = "light", Language = "ms-MY" };
        await using var auth = new FirebaseAuthenticationStateProvider(js);
        auth.OnAuthStateChanged(null);
        using var settings = new UserSettingsService(new BrowserStorageService(js), auth, js);
        await settings.UpdateThemeAsync("dark");
        await settings.UpdateLanguageAsync("zh-CN");

        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        Assert.Equal("ms-MY", (await settings.GetSettingsAsync()).Language);
        await settings.UpdateThemeAsync("dark");
        Assert.Equal("dark", js.Cloud["alice"].Theme);
        Assert.Equal("ms-MY", js.Cloud["alice"].Language);

        auth.OnAuthStateChanged(null);
        Assert.Equal("zh-CN", (await settings.GetSettingsAsync()).Language);
        Assert.Equal("dark", (await settings.GetSettingsAsync()).Theme);
    }

    [Fact]
    public async Task NewAccountDoesNotInheritAnotherAccountsSettings_OrWriteOnRead()
    {
        var js = new SettingsJsRuntime();
        js.Cloud["alice"] = new UserSettings { Language = "ms-MY", HasSeenWelcome = true };
        await using var auth = new FirebaseAuthenticationStateProvider(js);
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        using var settings = new UserSettingsService(new BrowserStorageService(js), auth, js);
        await settings.GetSettingsAsync();
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "bob" });

        var bob = await settings.GetSettingsAsync();
        Assert.Null(bob.Language);
        Assert.Null(bob.HasSeenWelcome);
        Assert.Empty(js.Writes);
        await settings.UpdateLanguageAsync("en-US");
        Assert.Equal("ms-MY", js.Cloud["alice"].Language);
        Assert.Equal("en-US", js.Cloud["bob"].Language);
    }

    [Fact]
    public async Task FailedCloudReadUsesOnlyThatAccountsCache_AndNeverOverwritesCloud()
    {
        var js = new SettingsJsRuntime();
        js.Cloud["alice"] = new UserSettings { Language = "ms-MY" };
        await using var auth = new FirebaseAuthenticationStateProvider(js);
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        using var settings = new UserSettingsService(new BrowserStorageService(js), auth, js);
        await settings.GetSettingsAsync();

        auth.OnAuthStateChanged(null);
        js.FailReads = true;
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "bob" });
        Assert.Null((await settings.GetSettingsAsync()).Language);
        Assert.NotNull(settings.SyncError);

        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        Assert.Equal("ms-MY", (await settings.GetSettingsAsync()).Language);
        Assert.Empty(js.Writes);
    }

    [Fact]
    public async Task FailedWriteKeepsLocalPreferencesAndReportsSyncFailure()
    {
        var js = new SettingsJsRuntime { FailWrites = true };
        await using var auth = new FirebaseAuthenticationStateProvider(js);
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        using var settings = new UserSettingsService(new BrowserStorageService(js), auth, js);
        await settings.UpdateThemeAsync("light");
        Assert.NotNull(settings.SyncError);
        Assert.Contains("light", js.Storage["preferences:user:alice"]);

        js.FailWrites = false;
        await settings.UpdateThemeAsync("light");
        await settings.UpdateLanguageAsync("ms-MY");
        Assert.Null(settings.SyncError);
        Assert.Equal("light", js.Cloud["alice"].Theme);
    }

    [Fact]
    public async Task AccountSwitchDuringCloudReadDiscardsOldResult()
    {
        var js = new SettingsJsRuntime();
        js.Cloud["alice"] = new UserSettings { Language = "ms-MY" };
        js.Cloud["bob"] = new UserSettings { Language = "en-US" };
        await using var auth = new FirebaseAuthenticationStateProvider(js);
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        using var settings = new UserSettingsService(new BrowserStorageService(js), auth, js);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        js.BeforeRead = async uid =>
        {
            if (uid != "alice") return;
            started.SetResult();
            await resume.Task;
        };

        var pending = settings.GetSettingsAsync();
        await started.Task;
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "bob" });
        resume.SetResult();

        Assert.Equal("en-US", (await pending).Language);
        Assert.Equal("en-US", (await settings.GetSettingsAsync()).Language);
        Assert.Empty(js.Writes);
    }

    [Fact]
    public async Task AccountSwitchDuringPreferenceUpdateDoesNotWriteToNewAccount()
    {
        var js = new SettingsJsRuntime();
        js.Cloud["alice"] = new UserSettings { Theme = "dark" };
        js.Cloud["bob"] = new UserSettings { Theme = "dark" };
        await using var auth = new FirebaseAuthenticationStateProvider(js);
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        using var settings = new UserSettingsService(new BrowserStorageService(js), auth, js);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        js.BeforeRead = async uid =>
        {
            if (uid != "alice") return;
            started.SetResult();
            await resume.Task;
        };

        var pending = settings.UpdateThemeAsync("light");
        await started.Task;
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "bob" });
        resume.SetResult();
        await pending;

        Assert.Empty(js.Writes);
        Assert.Equal("dark", js.Cloud["bob"].Theme);
    }

    [Fact]
    public async Task EditingAfterFailedReadPreservesUnrelatedCloudFields()
    {
        var js = new SettingsJsRuntime { FailReads = true };
        js.Cloud["alice"] = new UserSettings { Language = "ms-MY", HasSeenWelcome = true };
        await using var auth = new FirebaseAuthenticationStateProvider(js);
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        using var settings = new UserSettingsService(new BrowserStorageService(js), auth, js);

        await settings.UpdateThemeAsync("light");

        Assert.Equal("light", js.Cloud["alice"].Theme);
        Assert.Equal("ms-MY", js.Cloud["alice"].Language);
        Assert.True(js.Cloud["alice"].HasSeenWelcome);
    }

    [Fact]
    public async Task ConcurrentPreferenceChangesPreserveBothFields()
    {
        var js = new SettingsJsRuntime();
        await using var auth = new FirebaseAuthenticationStateProvider(js);
        auth.OnAuthStateChanged(new FirebaseUser { Uid = "alice" });
        using var settings = new UserSettingsService(new BrowserStorageService(js), auth, js);
        await Task.WhenAll(settings.UpdateThemeAsync("light"), settings.UpdateLanguageAsync("zh-CN"));
        Assert.Equal("light", js.Cloud["alice"].Theme);
        Assert.Equal("zh-CN", js.Cloud["alice"].Language);
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

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier.StartsWith("window.firebaseAuthInterop.")) return default!;
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
                case "window.firebaseFirestoreInterop.getUserSettings":
                    if (BeforeRead != null) await BeforeRead(key);
                    if (FailReads) throw new JSException("Offline");
                    if (Cloud.TryGetValue(key, out var settings)) result = settings with { };
                    break;
                case "window.firebaseFirestoreInterop.setUserSettings":
                    if (FailWrites) throw new JSException("Permission denied");
                    Writes.Add(key);
                    if (args[1] is UserSettings fullSettings)
                    {
                        Cloud[key] = fullSettings with { };
                    }
                    else
                    {
                        var patch = Assert.IsType<Dictionary<string, object>>(args[1]);
                        var saved = Cloud.GetValueOrDefault(key) ?? new UserSettings();
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
                        Cloud[key] = saved;
                    }
                    break;
                default:
                    throw new InvalidOperationException(identifier);
            }
            return result is null ? default! : (TValue)result;
        }
    }
}
