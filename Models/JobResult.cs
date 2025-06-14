namespace Models
{
    public class JobResult
    {
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
        public int ProcessedItems { get; set; }
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    }

    public class HealthStatus
    {
        public bool IsHealthy { get; set; }
        public DateTime LastSuccessTime { get; set; }
        public string? LastError { get; set; }
        public DateTime LastCheckTime { get; set; } = DateTime.UtcNow;
    }
}
