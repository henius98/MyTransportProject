namespace MyTransportAppWASM.Services.Interfaces
{
  public interface IGeocodingService
  {
    Task<(double Lat, double Lng, string FormattedAddress)?> GeocodeAsync(string address);
    Task<List<(double Lat, double Lng, string FormattedAddress, string Name)>> GetSuggestionsAsync(string address);
    Task<List<MyTransportAppWASM.Services.GeocodingService.MetLocation>> GetAllLocationsAsync();
    Task<string?> ReverseGeocodeAsync(double lat, double lng);
  }
}
