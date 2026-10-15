using System.Net.Http.Json;
using MyTransportAppWASM.Models.BaziFlow;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
    public class BaziFlowService : IBaziFlowService
    {
        private readonly HttpClient _httpClient;
        private readonly IBrowserStorageService _localStorage;
        private const string ApiKeyStorageKey = "baziflow_api_key";

        public BaziFlowService(HttpClient httpClient, IBrowserStorageService localStorage)
        {
            _httpClient = httpClient;
            _localStorage = localStorage;
        }



        public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            return await _localStorage.GetStringAsync(ApiKeyStorageKey);
        }

        public async Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            await _localStorage.SetStringAsync(ApiKeyStorageKey, apiKey);
        }

        public async Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default)
        {
            var key = await GetApiKeyAsync(cancellationToken);
            return !string.IsNullOrEmpty(key);
        }

        public async Task<ProfileData?> GetProfileAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetFromJsonAsync(
                    "/api/v1/profile",
                    MyTransportAppWASM.Models.AppJsonSerializerContext.Default.ApiResponseProfileData,
                    cancellationToken);
                return response?.Data;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching profile: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> CreateProfileAsync(CreateProfileRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    "/api/v1/profile",
                    request,
                    MyTransportAppWASM.Models.AppJsonSerializerContext.Default.CreateProfileRequest,
                    cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating profile: {ex.Message}");
                return false;
            }
        }

        public async Task<FortuneData?> GetDateFortuneAsync(string date, CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new DateFortuneRequest { Date = date };
                using var response = await _httpClient.PostAsJsonAsync(
                    "/api/v1/date-fortune",
                    request,
                    MyTransportAppWASM.Models.AppJsonSerializerContext.Default.DateFortuneRequest,
                    cancellationToken);
                
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync(
                        MyTransportAppWASM.Models.AppJsonSerializerContext.Default.ApiResponseFortuneData,
                        cancellationToken);
                    if (result?.Data != null)
                    {
                        if (result.Data.FavorableDirections == null || result.Data.FavorableDirections.Count == 0)
                        {
                            result.Data.FavorableDirections = new List<string> { "North", "East", "Southeast" };
                        }
                        if (result.Data.LuckyHours == null || result.Data.LuckyHours.Count == 0)
                        {
                            result.Data.LuckyHours = new List<int> { 7, 8, 9, 17, 18 };
                        }
                    }
                    return result?.Data;
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching date fortune: {ex.Message}");
                return null;
            }
        }
    }
}
