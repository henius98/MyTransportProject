using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
    public class UserSettingsService : IUserSettingsService
    {
        private readonly ILocalStorageService _localStorage;
        private readonly AuthenticationStateProvider _authStateProvider;
        private readonly IJSRuntime _jsRuntime;
        
        private UserSettings? _cachedSettings;

        public UserSettingsService(ILocalStorageService localStorage, AuthenticationStateProvider authStateProvider, IJSRuntime jsRuntime)
        {
            _localStorage = localStorage;
            _authStateProvider = authStateProvider;
            _jsRuntime = jsRuntime;
        }

        public async Task<UserSettings> GetSettingsAsync()
        {
            if (_cachedSettings != null)
                return _cachedSettings;

            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user.Identity?.IsAuthenticated == true)
            {
                var uid = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(uid))
                {
                    // Fetch from Firestore
                    var firestoreSettings = await _jsRuntime.InvokeAsync<UserSettings?>("window.firebaseFirestoreInterop.getUserSettings", uid);
                    
                    if (firestoreSettings != null)
                    {
                        // Sync down to local storage
                        await SyncToLocalStorage(firestoreSettings);
                        _cachedSettings = firestoreSettings;
                        return firestoreSettings;
                    }
                    else
                    {
                        // If no firestore settings exist yet, we should push local settings to Firestore
                        var localSettings = await GetLocalSettingsAsync();
                        await _jsRuntime.InvokeVoidAsync("window.firebaseFirestoreInterop.setUserSettings", uid, localSettings);
                        _cachedSettings = localSettings;
                        return localSettings;
                    }
                }
            }

            // Fallback to local
            _cachedSettings = await GetLocalSettingsAsync();
            return _cachedSettings;
        }

        public async Task SaveSettingsAsync(UserSettings settings)
        {
            _cachedSettings = settings;
            await SyncToLocalStorage(settings);

            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user.Identity?.IsAuthenticated == true)
            {
                var uid = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(uid))
                {
                    await _jsRuntime.InvokeVoidAsync("window.firebaseFirestoreInterop.setUserSettings", uid, settings);
                }
            }
        }

        public async Task UpdateThemeAsync(string theme)
        {
            var settings = await GetSettingsAsync();
            settings.Theme = theme;
            await SaveSettingsAsync(settings);
        }

        public async Task UpdateLanguageAsync(string language)
        {
            var settings = await GetSettingsAsync();
            settings.Language = language;
            await SaveSettingsAsync(settings);
        }

        public async Task UpdateHasSeenWelcomeAsync(bool hasSeen)
        {
            var settings = await GetSettingsAsync();
            settings.HasSeenWelcome = hasSeen;
            await SaveSettingsAsync(settings);
        }

        public async Task UpdateDefaultLocationAsync(UserLocation location)
        {
            var settings = await GetSettingsAsync();
            settings.DefaultLocation = location;
            await SaveSettingsAsync(settings);
        }

        private async Task<UserSettings> GetLocalSettingsAsync()
        {
            var settings = new UserSettings();
            
            // Read from the existing separate keys that the app uses for backwards compatibility
            var theme = await _localStorage.GetItemAsync<string>("theme-mode-global");
            if (!string.IsNullOrEmpty(theme)) settings.Theme = theme;

            var language = await _localStorage.GetItemAsync<string>("user_language");
            if (!string.IsNullOrEmpty(language)) settings.Language = language;

            var hasSeenWelcomeStr = await _localStorage.GetItemAsync<string>("hasSeenWelcome");
            if (hasSeenWelcomeStr == "true") settings.HasSeenWelcome = true;

            var customLat = await _localStorage.GetItemAsync<double?>("custom_lat");
            var customLng = await _localStorage.GetItemAsync<double?>("custom_lng");
            
            if (customLat.HasValue && customLng.HasValue)
            {
                settings.DefaultLocation = new UserLocation { Latitude = customLat.Value, Longitude = customLng.Value };
            }

            return settings;
        }

        private async Task SyncToLocalStorage(UserSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.Theme))
            {
                await _localStorage.SetItemAsync("theme-mode-global", settings.Theme);
            }
            if (!string.IsNullOrEmpty(settings.Language))
            {
                await _localStorage.SetItemAsync("user_language", settings.Language);
            }
            if (settings.HasSeenWelcome.HasValue)
            {
                await _localStorage.SetItemAsync("hasSeenWelcome", settings.HasSeenWelcome.Value ? "true" : "false");
            }
            if (settings.DefaultLocation != null)
            {
                await _localStorage.SetItemAsync("custom_lat", settings.DefaultLocation.Latitude);
                await _localStorage.SetItemAsync("custom_lng", settings.DefaultLocation.Longitude);
            }
        }
    }
}
