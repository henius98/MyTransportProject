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



        public async Task<string?> GetApiKeyAsync()
        {
            return await _localStorage.GetStringAsync(ApiKeyStorageKey);
        }

        public async Task SaveApiKeyAsync(string apiKey)
        {
            await _localStorage.SetStringAsync(ApiKeyStorageKey, apiKey);
        }

        public async Task<bool> HasApiKeyAsync()
        {
            var key = await GetApiKeyAsync();
            return !string.IsNullOrEmpty(key);
        }

        public async Task<ProfileData?> GetProfileAsync()
        {

            try
            {
                var response = await _httpClient.GetFromJsonAsync(
                    "/api/v1/profile",
                    MyTransportAppWASM.Models.AppJsonSerializerContext.Default.ApiResponseProfileData);
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

            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    "/api/v1/profile",
                    request,
                    MyTransportAppWASM.Models.AppJsonSerializerContext.Default.CreateProfileRequest);
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

            try
            {
                var request = new DateFortuneRequest { Date = date };
                using var response = await _httpClient.PostAsJsonAsync(
                    "/api/v1/date-fortune",
                    request,
                    MyTransportAppWASM.Models.AppJsonSerializerContext.Default.DateFortuneRequest);
                
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync(
                        MyTransportAppWASM.Models.AppJsonSerializerContext.Default.ApiResponseFortuneData);
                    if (result?.Data != null)
                    {
                        result.Data.FavorableDirections = new List<string> { "North", "East", "Southeast" };
                        result.Data.LuckyHours = new List<int> { 7, 8, 9, 17, 18 };
                    }
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
