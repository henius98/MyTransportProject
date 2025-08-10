using System.ComponentModel.DataAnnotations;

namespace GTFSRealtimeAppSettings
{
    public class GTFSRealtimeApiSettings
    {
        public PrasaranaSettings Prasarana { get; set; } = new();
    }

    public class PrasaranaSettings
    {
        [Required]
        [Url]
        public string RapidKL { get; set; } = string.Empty;

        [Required]
        [Url]
        public string RapidPenang { get; set; } = string.Empty;
    }

    public class AppSettings
    {
        public int PollingIntervalSeconds { get; set; } = 30;
        public int MaxRetryAttempts { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 5;
        public bool EnableHealthMonitoring { get; set; } = false;
    }
}
