using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using System.Security.Claims;
using System.Text.Json;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
    public class UserSettingsService : IUserSettingsService, IDisposable
    {
        private readonly IBrowserStorageService _localStorage;
        private readonly AuthenticationStateProvider _authStateProvider;
        private readonly IJSRuntime _jsRuntime;
        
        private UserSettings? _cachedSettings;
        private string? _cachedUserId;
        private readonly SemaphoreSlim _settingsLock = new(1, 1);

        public string? SyncError { get; private set; }
        public event Action? SyncStatusChanged;

        public UserSettingsService(IBrowserStorageService localStorage, AuthenticationStateProvider authStateProvider, IJSRuntime jsRuntime)
        {
            _localStorage = localStorage;
            _authStateProvider = authStateProvider;
            _jsRuntime = jsRuntime;
            _authStateProvider.AuthenticationStateChanged += HandleAuthenticationStateChanged;
        }

        public async Task<UserSettings> GetSettingsAsync()
        {
            await _settingsLock.WaitAsync();
            try
            {
                var (_, settings) = await LoadCurrentSettingsAsync();
                return settings with { DefaultLocation = settings.DefaultLocation is { } location ? location with { } : null };
            }
            finally
            {
                _settingsLock.Release();
            }
        }

        private async Task<string?> GetUserIdAsync()
        {
            var user = (await _authStateProvider.GetAuthenticationStateAsync()).User;
            return user.Identity?.IsAuthenticated == true ? user.FindFirst(ClaimTypes.NameIdentifier)?.Value : null;
        }

        private async Task<(string? Uid, UserSettings Settings)> LoadCurrentSettingsAsync()
        {
            while (true)
            {
                var uid = await GetUserIdAsync();
                var settings = await LoadSettingsAsync(uid);
                if (!string.Equals(uid, await GetUserIdAsync(), StringComparison.Ordinal))
                    continue;

                _cachedSettings = settings;
                _cachedUserId = uid;
                return (uid, settings);
            }
        }

        private async Task<UserSettings> LoadSettingsAsync(string? uid)
        {
            if (_cachedSettings != null && string.Equals(_cachedUserId, uid, StringComparison.Ordinal))
                return _cachedSettings;

            if (!string.IsNullOrEmpty(uid))
            {
                await GetLocalSettingsAsync(null);
                try
                {
                    var firestoreSettings = await _jsRuntime.InvokeAsync<UserSettings?>("window.firebaseFirestoreInterop.getUserSettings", TimeSpan.FromSeconds(10), uid);
                    
                    if (firestoreSettings != null)
                    {
                        await SyncToLocalStorage(uid, firestoreSettings);
                        SetSyncError(null);
                        return firestoreSettings;
                    }

                    SetSyncError(null);
                }
                catch (Exception ex) when (ex is JSException or OperationCanceledException)
                {
                    // A failed read must not be treated as a missing document, otherwise stale
                    // local settings could overwrite a user's Firestore settings.
                    Console.Error.WriteLine($"Could not load cloud settings; using this device's settings: {ex.Message}");
                    SetSyncError("Cloud preferences are unavailable. Using settings saved on this device.");
                }
            }

            return await GetLocalSettingsAsync(uid);
        }

        public async Task SaveSettingsAsync(UserSettings settings)
        {
            var uid = await GetUserIdAsync();
            await _settingsLock.WaitAsync();
            try
            {
                if (uid != await GetUserIdAsync()) return;
                await SaveSettingsForUserAsync(uid, settings);
            }
            finally
            {
                _settingsLock.Release();
            }
        }

        private async Task SaveSettingsForUserAsync(string? uid, UserSettings settings, object? changes = null)
        {
            await SyncToLocalStorage(uid, settings);

            if (!string.IsNullOrEmpty(uid))
            {
                try
                {
                    await _jsRuntime.InvokeVoidAsync("window.firebaseFirestoreInterop.setUserSettings", TimeSpan.FromSeconds(10), uid, changes ?? settings);
                    if (uid == await GetUserIdAsync()) SetSyncError(null);
                }
                catch (Exception ex) when (ex is JSException or OperationCanceledException)
                {
                    Console.Error.WriteLine($"Settings were saved on this device but not synced: {ex.Message}");
                    if (uid == await GetUserIdAsync())
                        SetSyncError("Preferences saved on this device. Cloud sync failed; try changing the preference again when connected.");
                }
            }

            if (uid == await GetUserIdAsync())
            {
                _cachedSettings = settings;
                _cachedUserId = uid;
            }
        }

        public async Task UpdateThemeAsync(string theme)
        {
            await UpdateSettingsAsync(settings => settings with { Theme = theme }, "theme", theme);
        }

        public async Task UpdateLanguageAsync(string language)
        {
            await UpdateSettingsAsync(settings => settings with { Language = language }, "language", language);
        }

        public async Task UpdateHasSeenWelcomeAsync(bool hasSeen)
        {
            await UpdateSettingsAsync(settings => settings with { HasSeenWelcome = hasSeen }, "hasSeenWelcome", hasSeen);
        }

        public async Task UpdateDefaultLocationAsync(UserLocation location)
        {
            await UpdateSettingsAsync(settings => settings with { DefaultLocation = location with { } }, "defaultLocation", location);
        }

        private async Task UpdateSettingsAsync(Func<UserSettings, UserSettings> update, string field, object value)
        {
            var requestedUid = await GetUserIdAsync();
            await _settingsLock.WaitAsync();
            try
            {
                var (uid, settings) = await LoadCurrentSettingsAsync();
                if (uid != requestedUid) return;
                // Only merge the edited field, so a failed read or another device's changes
                // cannot cause unrelated cloud preferences to be overwritten.
                await SaveSettingsForUserAsync(uid, update(settings), new Dictionary<string, object> { [field] = value });
            }
            finally
            {
                _settingsLock.Release();
            }
        }

        private static string StorageKey(string? uid) =>
            uid is null ? "preferences:guest" : $"preferences:user:{uid}";

        private async Task<UserSettings> GetLocalSettingsAsync(string? uid)
        {
            var json = await _localStorage.GetStringAsync(StorageKey(uid));
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UserSettings) ?? new UserSettings();
                }
                catch (JsonException)
                {
                    // Ignore a corrupt local cache; cloud settings remain the source of truth.
                }
            }

            if (uid is not null) return new UserSettings();

            var settings = new UserSettings();
            
            // Read existing separate keys once for backwards-compatible guest preferences only.
            var theme = await _localStorage.GetStringAsync("theme-mode-global");
            if (!string.IsNullOrEmpty(theme)) settings.Theme = theme;

            var language = await _localStorage.GetStringAsync("user_language");
            if (!string.IsNullOrEmpty(language)) settings.Language = language;

            var hasSeenWelcomeStr = await _localStorage.GetStringAsync("hasSeenWelcome");
            if (hasSeenWelcomeStr == "true") settings.HasSeenWelcome = true;

            var customLat = await _localStorage.GetDoubleAsync("custom_lat");
            var customLng = await _localStorage.GetDoubleAsync("custom_lng");
            
            if (customLat.HasValue && customLng.HasValue)
            {
                settings.DefaultLocation = new UserLocation { Latitude = customLat.Value, Longitude = customLng.Value };
            }

            await SyncToLocalStorage(uid, settings);
            return settings;
        }

        private async Task SyncToLocalStorage(string? uid, UserSettings settings)
        {
            await _localStorage.SetStringAsync(StorageKey(uid),
                JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.UserSettings));
        }

        private void HandleAuthenticationStateChanged(Task<AuthenticationState> _)
        {
            _cachedSettings = null;
            _cachedUserId = null;
            SetSyncError(null);
        }

        private void SetSyncError(string? message)
        {
            if (SyncError == message) return;
            SyncError = message;
            SyncStatusChanged?.Invoke();
        }

        public void Dispose()
        {
            _authStateProvider.AuthenticationStateChanged -= HandleAuthenticationStateChanged;
        }
    }
}
