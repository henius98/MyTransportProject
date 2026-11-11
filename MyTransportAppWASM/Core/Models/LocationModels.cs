using System.Text.Json.Serialization;

namespace MyTransportAppWASM.Core.Models
{
  public sealed class MetLocation
  {
    [JsonPropertyName("location_id")]
    public string? LocationId { get; set; }

    [JsonPropertyName("location_name")]
    public string? LocationName { get; set; }

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
  }

  public sealed class GeocodeResult
  {
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }

    [JsonPropertyName("formattedAddress")]
    public string FormattedAddress { get; set; } = string.Empty;
  }
}


