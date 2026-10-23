using MyTransportAppWASM.Models.BaziFlow;

namespace MyTransportAppWASM.Services.Interfaces
{
    public interface IBaziFlowService
    {
        Task<ProfileData?> GetProfileAsync(CancellationToken cancellationToken = default);
        Task<bool> CreateProfileAsync(CreateProfileRequest request, CancellationToken cancellationToken = default);
        Task<FortuneData?> GetDateFortuneAsync(string date, CancellationToken cancellationToken = default);
        Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);
        Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);
        Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default);
    }
}
