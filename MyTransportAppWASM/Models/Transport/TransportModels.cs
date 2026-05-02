namespace MyTransportAppWASM.Models.Transport
{
    public record TransportProvider(string Name, string Endpoint, double CenterLat, double CenterLng, double RadiusKm);
}
