namespace MyTransportAppWASM.Services.Interfaces
{
    public interface IGeocodingService
    {
        Task<(double Lat, double Lng, string FormattedAddress)?> GeocodeAsync(string address);
    }
}
