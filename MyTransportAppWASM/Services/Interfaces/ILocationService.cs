namespace MyTransportAppWASM.Services.Interfaces
{
    public record Location(double Latitude, double Longitude, double? Accuracy = null);

    public interface ILocationService
    {
        Task<Location?> GetCurrentLocationAsync();
        event Action<Location>? OnLocationChanged;
        Location? LastKnownLocation { get; }
    }
}
