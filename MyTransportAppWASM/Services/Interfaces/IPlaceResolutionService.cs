namespace MyTransportAppWASM.Services.Interfaces
{
  /// <summary>
  /// Universal seam for geographic coordinate resolution, suggestions, and reverse geocoding.
  /// </summary>
  public interface IPlaceResolutionService
  {
    Task<(double Lat, double Lng, string FormattedAddress)?> GeocodeAsync(string address, CancellationToken cancellationToken = default);
    Task<List<(double Lat, double Lng, string FormattedAddress, string Name)>> GetSuggestionsAsync(string address, CancellationToken cancellationToken = default);
    Task<string?> ReverseGeocodeAsync(double lat, double lng, CancellationToken cancellationToken = default);
  }
}
