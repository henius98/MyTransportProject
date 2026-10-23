namespace MyTransportAppWASM.Models
{
    public record UserLocation
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public record UserSettings
    {
        public string? Theme { get; set; }
        public string? Language { get; set; }
        public bool? HasSeenWelcome { get; set; }
        public UserLocation? DefaultLocation { get; set; }
    }
}
