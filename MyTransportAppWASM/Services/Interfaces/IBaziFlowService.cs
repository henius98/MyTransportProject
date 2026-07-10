using MyTransportAppWASM.Models.BaziFlow;

namespace MyTransportAppWASM.Services.Interfaces
{
    public interface IBaziFlowService
    {
        Task<ProfileData?> GetProfileAsync();
        Task<bool> CreateProfileAsync(CreateProfileRequest request);
        Task<FortuneData?> GetDateFortuneAsync(string date);
        Task SaveApiKeyAsync(string apiKey);
        Task<string?> GetApiKeyAsync();
        Task<bool> HasApiKeyAsync();
    }
}
