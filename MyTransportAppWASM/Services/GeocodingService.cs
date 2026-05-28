using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
    /// <summary>
    /// Implements geocoding using the Google Places API (New) Search Text endpoint.
    /// This is compatible with the "Places API (New)" setting in Google Cloud Console.
    /// </summary>
    public class GeocodingService : IGeocodingService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public GeocodingService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _apiKey = configuration["GoogleMaps:ApiKey"] ?? string.Empty;
        }

        public async Task<(double Lat, double Lng, string FormattedAddress)?> GeocodeAsync(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(_apiKey))
                return null;

            // Using the Places API (New) Text Search endpoint
            const string url = "https://places.googleapis.com/v1/places:searchText";

            try
            {
                var requestBody = new { textQuery = address };
                
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-Goog-Api-Key", _apiKey);
                // Field mask is required for Places API (New)
                request.Headers.Add("X-Goog-FieldMask", "places.formattedAddress,places.location,places.displayName");
                
                request.Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody), 
                    Encoding.UTF8, 
                    "application/json");

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<PlacesNewResponse>();
                    if (data?.Places?.Length > 0)
                    {
                        var place = data.Places[0];
                        return (place.Location.Latitude, place.Location.Longitude, place.FormattedAddress);
                    }
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.Error.WriteLine($"Places API (New) error: {response.StatusCode} - {errorContent}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Geocoding via Places (New) failed: {ex.Message}");
            }

            return null;
        }

        private class PlacesNewResponse
        {
            [JsonPropertyName("places")]
            public Place[]? Places { get; set; }
        }

        private class Place
        {
            [JsonPropertyName("formattedAddress")]
            public string FormattedAddress { get; set; } = string.Empty;

            [JsonPropertyName("location")]
            public LatLng Location { get; set; } = new();
            
            [JsonPropertyName("displayName")]
            public DisplayName? DisplayName { get; set; }
        }

        private class LatLng
        {
            [JsonPropertyName("latitude")]
            public double Latitude { get; set; }

            [JsonPropertyName("longitude")]
            public double Longitude { get; set; }
        }

        private class DisplayName
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;
        }
    }
}
