using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
  /// <summary>
  /// Implements geocoding using the Google Places API (New) Search Text endpoint.
  /// This is compatible with the "Places API (New)" setting in Google Cloud Console.
  /// Adapts to IPlaceResolutionService and IWeatherStationCatalogService.
  /// </summary>
  public class GeocodingService : IGeocodingService
  {
    private readonly IPlaceResolutionService _placeResolution;
    private readonly IWeatherStationCatalogService _stationCatalog;

    public GeocodingService(HttpClient httpClient, IConfiguration configuration)
    {
      var stationCatalog = new WeatherStationCatalogService(httpClient);
      _stationCatalog = stationCatalog;
      _placeResolution = new PlaceResolutionService(httpClient, configuration, stationCatalog);
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public GeocodingService(IPlaceResolutionService placeResolution, IWeatherStationCatalogService stationCatalog)
    {
      _placeResolution = placeResolution;
      _stationCatalog = stationCatalog;
    }

    // Using the Places API (New) Text Search endpoint
    // Field mask is required for Places API (New)
    public Task<(double Lat, double Lng, string FormattedAddress)?> GeocodeAsync(string address) =>
      _placeResolution.GeocodeAsync(address);

    public Task<List<(double Lat, double Lng, string FormattedAddress, string Name)>> GetSuggestionsAsync(string address) =>
      _placeResolution.GetSuggestionsAsync(address);

    // Allow a later call to retry after a transient static-asset failure.
    public async Task<List<MetLocation>> GetAllLocationsAsync()
    {
      var stations = await _stationCatalog.GetAllStationsAsync();
      return stations as List<MetLocation> ?? stations.ToList();
    }

    public Task<string?> ReverseGeocodeAsync(double lat, double lng) =>
      _placeResolution.ReverseGeocodeAsync(lat, lng);
  }
}
