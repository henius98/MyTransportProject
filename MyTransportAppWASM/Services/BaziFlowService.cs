using System.Net.Http.Headers;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using MyTransportAppWASM.Models.BaziFlow;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
    public class BaziFlowService : IBaziFlowService
    {
        private readonly HttpClient _httpClient;
        private readonly ILocalStorageService _localStorage;
        private const string ApiKeyStorageKey = "baziflow_api_key";

        public BaziFlowService(HttpClient httpClient, ILocalStorageService localStorage)
        {
            _httpClient = httpClient;
            _localStorage = localStorage;
        }

        private async Task EnsureAuthHeaderAsync()
        {
            var apiKey = await GetApiKeyAsync();
            if (!string.IsNullOrEmpty(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        public async Task<string?> GetApiKeyAsync()
        {
            return await _localStorage.GetItemAsync<string>(ApiKeyStorageKey);
        }

        public async Task SaveApiKeyAsync(string apiKey)
        {
            await _localStorage.SetItemAsync(ApiKeyStorageKey, apiKey);
        }

        public async Task<bool> HasApiKeyAsync()
        {
            var key = await GetApiKeyAsync();
            return !string.IsNullOrEmpty(key);
        }

        public async Task<ProfileData?> GetProfileAsync()
        {
            await EnsureAuthHeaderAsync();
            try
            {
                var response = await _httpClient.GetFromJsonAsync<ApiResponse<ProfileData>>("/api/v1/profile");
                return response?.Data;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching profile: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> CreateProfileAsync(CreateProfileRequest request)
        {
            await EnsureAuthHeaderAsync();
            try
            {
                var response = await _httpClient.PostAsJsonAsync("/api/v1/profile", request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating profile: {ex.Message}");
                return false;
            }
        }

        public async Task<FortuneData?> GetDateFortuneAsync(string date)
        {
            await EnsureAuthHeaderAsync();
            try
            {
                var request = new DateFortuneRequest { Date = date };
                var response = await _httpClient.PostAsJsonAsync("/api/v1/date-fortune", request);
                
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ApiResponse<FortuneData>>();
                    return result?.Data;
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching date fortune: {ex.Message}");
                return null;
            }
        }
    }
}
