namespace MyTransportAppWASM.Core.Models;

public sealed class DefaultLocationOptions
{
  public const string SectionName = "DefaultLocation";

  public double? Latitude { get; set; }
  public double? Longitude { get; set; }

  public (double Latitude, double Longitude) ResolveCoordinates() => (
    Latitude ?? throw new InvalidOperationException("Missing configuration: DefaultLocation:Latitude"),
    Longitude ?? throw new InvalidOperationException("Missing configuration: DefaultLocation:Longitude"));
}
