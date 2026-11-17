namespace MyTransportAppWASM.Features.Transit.Models
{
  public record TransportProvider(string Name, string Endpoint, double CenterLat, double CenterLng, double RadiusKm, string TransportType = "Bus", TransportStaticData? StaticData = null)
  {
    public TransportProvider() : this("", "", 0, 0, 0) { }

    public bool HasRealtimeFeed => !string.IsNullOrWhiteSpace(Endpoint);
    public bool HasStaticData => !string.IsNullOrWhiteSpace(StaticData?.BaseUrl);
  }

  public record TransportStaticData(string BaseUrl, string RouteInfoPath, string GeoJSONUrlPath, string DeparturePath);

  public record StaticRoute(string Id, string Name);

  public record StaticDeparture(string RouteId, string? Headsign, DateTimeOffset? DepartureAt);

  // Models for Live Routing Service
  public record GoogleRoute
  {
    public int RouteId { get; set; }
    public string EncodedPolyline { get; set; } = "";
    public int DurationSeconds { get; set; }
    public int DistanceMeters { get; set; }
    public string LocalizedDuration { get; set; } = "";
    public string LocalizedDistance { get; set; } = "";
    public string DepartureTimeText { get; set; } = "";
    public string ArrivalTimeText { get; set; } = "";
    public int? HeadwayMinutes { get; set; }
    public List<TransitStep> TransitSteps { get; set; } = new();
    public List<RouteSegment> Segments { get; set; } = new();
  }

  public record RouteSegment
  {
    public string TravelMode { get; set; } = "";
    public string VehicleType { get; set; } = "";
    public string LineShortName { get; set; } = "";
    public string LineName { get; set; } = "";
    public string LineColor { get; set; } = "";
    public string LineTextColor { get; set; } = "";
    public string Headsign { get; set; } = "";
    public string DepartureStopName { get; set; } = "";
    public string ArrivalStopName { get; set; } = "";
    public string DepartureTimeText { get; set; } = "";
    public string ArrivalTimeText { get; set; } = "";
    public int DurationSeconds { get; set; }
    public int DistanceMeters { get; set; }
    public int StopCount { get; set; }
    public int? HeadwayMinutes { get; set; }
    public string Instructions { get; set; } = "";
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
    [System.Text.Json.Serialization.JsonPropertyName("providerName")]
    public string? ProviderName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("transportType")]
    public string? TransportType { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("routeId")]
    public string? RouteId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("routeShortName")]
    public string? RouteShortName { get; set; }
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

  public record TransitTrackingResult
  {
    public IReadOnlyList<BusLocation> Vehicles { get; init; } = Array.Empty<BusLocation>();
    public string StatusMessage { get; init; } = string.Empty;
    public DateTimeOffset? LastUpdated { get; init; }
    public int AttemptedProviderCount { get; init; }
    public int FailedProviderCount { get; init; }
    public int StaleProviderCount { get; init; }
    public int ExpiredProviderCount { get; init; }
    public int NearbyProviderCount { get; init; }
    public bool HasNearbyFeeds { get; init; }
  }
}
