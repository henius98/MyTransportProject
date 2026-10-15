using System.Text.Json.Serialization;

namespace MyTransportAppWASM.Models
{
  public class MetLocation
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

  internal record GeocodingRequest
  {
    [JsonPropertyName("textQuery")]
    public string TextQuery { get; set; } = string.Empty;
  }

  internal class PlacesNewResponse
  {
    [JsonPropertyName("places")]
    public Place[]? Places { get; set; }
  }

  internal class Place
  {
    [JsonPropertyName("formattedAddress")]
    public string FormattedAddress { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public LatLng Location { get; set; } = new();

    [JsonPropertyName("displayName")]
    public DisplayName? DisplayName { get; set; }
  }

  internal class LatLng
  {
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
  }

  internal class DisplayName
  {
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
  }
}

