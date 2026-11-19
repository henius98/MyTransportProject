namespace MyTransportAppWASM.Core.Services
{
  public record Location(double Latitude, double Longitude, double? Accuracy = null);

  public interface ILocationService
  {
    Task<Location?> GetCurrentLocationAsync(bool forceRefresh = false);
    void UpdateLocation(double latitude, double longitude, double? accuracy = null);
    Location? LastKnownLocation { get; }
  }
}
