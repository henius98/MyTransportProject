namespace MyTransportAppWASM.Models
{
  public record TransportProvider(string Name, string Endpoint, double CenterLat, double CenterLng, double RadiusKm);

  // Models for Live Routing Service
  public record GoogleRoute
  {
    public int RouteId { get; set; }
    public string EncodedPolyline { get; set; } = "";
    public int DurationSeconds { get; set; }
    public int DistanceMeters { get; set; }
    public List<TransitStep> TransitSteps { get; set; } = new();
  }

  public record TransitStep
  {
    public string LineShortName { get; set; } = "";
    public string LineName { get; set; } = "";
    public double DepartureLat { get; set; }
    public double DepartureLng { get; set; }
    public string DepartureStopName { get; set; } = "";
  }

  public record BusLocation
  {
    [System.Text.Json.Serialization.JsonPropertyName("routeId")]
    public string? RouteId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("vehicleId")]
    public string? VehicleId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("lat")]
    public float Lat { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("lng")]
    public float Lng { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("bearing")]
    public float Bearing { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("speed")]
    public float Speed { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public ulong Timestamp { get; set; }
  }

  public record LiveBusInfo
  {
    public string VehicleId { get; set; } = "";
    public string RouteId { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double DistanceToStopMeters { get; set; }
    public int EstimatedArrivalSeconds { get; set; }
  }

    public record EnrichedRoute
    {
        public GoogleRoute OriginalRoute { get; set; } = new();
        public int LiveTotalDurationSeconds { get; set; }
        public List<LiveBusInfo> NearestBuses { get; set; } = new();
        public bool HasLiveBus { get; set; }
        public bool IsAuspicious { get; set; }
    }
}
