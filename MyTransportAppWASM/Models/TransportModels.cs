namespace MyTransportAppWASM.Models
{
  public record TransportProvider(string Name, string Endpoint, double CenterLat, double CenterLng, double RadiusKm);
}
