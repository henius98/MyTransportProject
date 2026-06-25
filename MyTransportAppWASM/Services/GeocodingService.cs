using System.Text;
using System.Text.Json.Serialization;

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
        var requestBody = new GeocodingRequest { TextQuery = address };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Goog-Api-Key", _apiKey);
        // Field mask is required for Places API (New)
        request.Headers.Add("X-Goog-FieldMask", "places.formattedAddress,places.location,places.displayName");

        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(requestBody, MyTransportAppWASM.Models.AppJsonSerializerContext.Default.GeocodingRequest),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
          var data = await response.Content.ReadFromJsonAsync(MyTransportAppWASM.Models.AppJsonSerializerContext.Default.PlacesNewResponse);
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

    private List<MetLocation>? _cachedMetLocations;

    public async Task<List<(double Lat, double Lng, string FormattedAddress, string Name)>> GetSuggestionsAsync(string address)
    {
      if (string.IsNullOrWhiteSpace(address))
        return new();

      try
      {
        if (_cachedMetLocations == null)
        {
          _cachedMetLocations = await _httpClient.GetFromJsonAsync("data/recreation_centres.json", MyTransportAppWASM.Models.AppJsonSerializerContext.Default.ListMetLocation);
        }

        if (_cachedMetLocations != null)
        {
          return _cachedMetLocations
            .Where(l => l.LocationName != null && l.LocationName.Contains(address, StringComparison.OrdinalIgnoreCase))
            .Take(15)
            .Select(l => (l.Latitude, l.Longitude, $"MET ID: {l.LocationId}", l.LocationName!))
            .ToList();
        }
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Failed to load MetMalaysia locations: {ex.Message}");
      }

      return new();
    }

    public async Task<List<MetLocation>> GetAllLocationsAsync()
    {
      try
      {
        if (_cachedMetLocations == null)
        {
          _cachedMetLocations = await _httpClient.GetFromJsonAsync("data/recreation_centres.json", MyTransportAppWASM.Models.AppJsonSerializerContext.Default.ListMetLocation);
        }
        return _cachedMetLocations ?? new();
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Failed to load MetMalaysia locations for map: {ex.Message}");
      }
      return new();
    }

    public async Task<string?> ReverseGeocodeAsync(double lat, double lng)
    {
      if (string.IsNullOrWhiteSpace(_apiKey)) return null;

      string url = $"https://maps.googleapis.com/maps/api/geocode/json?latlng={lat},{lng}&key={_apiKey}";
      
      try
      {
        var response = await _httpClient.GetAsync(url);
        if (response.IsSuccessStatusCode)
        {
          var jsonString = await response.Content.ReadAsStringAsync();
          using var document = System.Text.Json.JsonDocument.Parse(jsonString);
          var root = document.RootElement;
          
          if (root.TryGetProperty("status", out var status) && status.GetString() == "OK")
          {
            if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
              var firstResult = results[0];
              if (firstResult.TryGetProperty("formatted_address", out var formattedAddress))
              {
                return formattedAddress.GetString();
              }
            }
          }
        }
        else
        {
          string errorContent = await response.Content.ReadAsStringAsync();
          Console.Error.WriteLine($"Geocoding API error: {response.StatusCode} - {errorContent}");
        }
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Reverse Geocoding failed: {ex.Message}");
      }
      
      return null;
    }

    internal record GeocodingRequest
    {
      [JsonPropertyName("textQuery")]
      public string TextQuery { get; set; } = string.Empty;
    }

    internal class PlacesNewResponse
    {
      [JsonPropertyName("places")]
      public Place[]? Places { get; set; }
    }

    internal class Place
    {
      [JsonPropertyName("formattedAddress")]
      public string FormattedAddress { get; set; } = string.Empty;

      [JsonPropertyName("location")]
      public LatLng Location { get; set; } = new();

      [JsonPropertyName("displayName")]
      public DisplayName? DisplayName { get; set; }
    }

    internal class LatLng
    {
      [JsonPropertyName("latitude")]
      public double Latitude { get; set; }

      [JsonPropertyName("longitude")]
      public double Longitude { get; set; }
    }

    internal class DisplayName
    {
      [JsonPropertyName("text")]
      public string Text { get; set; } = string.Empty;
    }

    public class MetLocation
    {
      [JsonPropertyName("location_id")]
      public string? LocationId { get; set; }

      [JsonPropertyName("location_name")]
      public string? LocationName { get; set; }

      [JsonPropertyName("latitude")]
      public double Latitude { get; set; }

      [JsonPropertyName("longitude")]
      public double Longitude { get; set; }
    }
  }
}
