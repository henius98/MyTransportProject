using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
  /// <summary>
  /// Unified place resolution service: resolves coordinate inputs,
  /// searches the weather station catalog, and queries Google Places (New) / Geocoding APIs.
  /// </summary>
  public class PlaceResolutionService : IPlaceResolutionService
  {
    private readonly HttpClient _httpClient;
    private readonly IWeatherStationCatalogService _stationCatalog;
    private readonly string _apiKey;

    public PlaceResolutionService(
      HttpClient httpClient,
      IConfiguration configuration,
      IWeatherStationCatalogService stationCatalog)
    {
      _httpClient = httpClient;
      _stationCatalog = stationCatalog;
      _apiKey = configuration["GoogleMaps:ApiKey"] ?? string.Empty;
    }

    public async Task<(double Lat, double Lng, string FormattedAddress)?> GeocodeAsync(string address, CancellationToken cancellationToken = default)
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

        request.Content = JsonContent.Create(requestBody, AppJsonSerializerContext.Default.GeocodingRequest);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
          var data = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.PlacesNewResponse, cancellationToken);
          if (data?.Places?.Length > 0)
          {
            var place = data.Places[0];
            return (place.Location.Latitude, place.Location.Longitude, place.FormattedAddress);
          }
        }
        else
        {
          string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
          Console.Error.WriteLine($"Places API (New) error: {response.StatusCode} - {errorContent}");
        }
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Geocoding via Places (New) failed: {ex.Message}");
      }

      return null;
    }

    public async Task<List<(double Lat, double Lng, string FormattedAddress, string Name)>> GetSuggestionsAsync(string address, CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrWhiteSpace(address))
        return new();

      address = address.Trim();
      var coordinates = address.Contains(',')
        ? address.Split(',', StringSplitOptions.TrimEntries)
        : address.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (coordinates.Length == 2
        && double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
        && double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
      {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)
          || latitude is < -90 or > 90 || longitude is < -180 or > 180)
          return new();

        return [(latitude, longitude, "Latitude, longitude", FormattableString.Invariant($"{latitude}, {longitude}"))];
      }

      try
      {
        var stations = await _stationCatalog.SearchStationsAsync(address, cancellationToken: cancellationToken);
        if (stations.Count > 0)
        {
          return stations
            .Select(l => (l.Latitude, l.Longitude, $"MET ID: {l.LocationId}", l.LocationName ?? l.LocationId!))
            .ToList();
        }
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Failed to query weather stations for suggestions: {ex.Message}");
      }

      // If no static station matched and API key is present, resolve real place via Google Places
      if (!string.IsNullOrWhiteSpace(_apiKey))
      {
        var place = await GeocodeAsync(address, cancellationToken);
        if (place != null)
        {
          return [(place.Value.Lat, place.Value.Lng, place.Value.FormattedAddress, address)];
        }
      }

      return new();
    }

    public async Task<string?> ReverseGeocodeAsync(double lat, double lng, CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrWhiteSpace(_apiKey) ||
          !double.IsFinite(lat) || !double.IsFinite(lng) ||
          lat is < -90 or > 90 || lng is < -180 or > 180)
      {
        return null;
      }

      string url = FormattableString.Invariant($"https://maps.googleapis.com/maps/api/geocode/json?latlng={lat},{lng}");

      try
      {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Goog-Api-Key", _apiKey);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
          await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
          using var document = await System.Text.Json.JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
          var root = document.RootElement;

          if (root.TryGetProperty("status", out var status))
          {
            var statusValue = status.GetString();
            if (statusValue == "OK")
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
            else if (statusValue != "ZERO_RESULTS")
            {
              Console.Error.WriteLine($"Geocoding API status: {statusValue}");
            }
          }
        }
        else
        {
          string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
          Console.Error.WriteLine($"Geocoding API error: {response.StatusCode} - {errorContent}");
        }
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Reverse Geocoding failed: {ex.Message}");
      }

      return null;
    }
  }
}
