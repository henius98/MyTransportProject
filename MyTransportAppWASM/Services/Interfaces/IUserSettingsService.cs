using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services.Interfaces
{
    public interface IUserSettingsService
    {
        Task<UserSettings> GetSettingsAsync();
        Task SaveSettingsAsync(UserSettings settings);
        Task UpdateThemeAsync(string theme);
        Task UpdateLanguageAsync(string language);
        Task UpdateHasSeenWelcomeAsync(bool hasSeen);
        Task UpdateDefaultLocationAsync(UserLocation location);
    }
}
