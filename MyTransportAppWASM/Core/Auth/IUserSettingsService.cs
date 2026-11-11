
namespace MyTransportAppWASM.Core.Auth
{
    public interface IUserSettingsService
    {
        string? SyncError { get; }
        event Action? SyncStatusChanged;
        Task<UserSettings> GetSettingsAsync();
        Task UpdateThemeAsync(string theme);
        Task UpdateLanguageAsync(string language);
        Task UpdateHasSeenWelcomeAsync(bool hasSeen);
    }
}
