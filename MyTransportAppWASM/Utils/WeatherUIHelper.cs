using System;

namespace MyTransportAppWASM.Utils
{
    public static class WeatherUIHelper
    {
        public static string GetStatusClass(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "status--muted";
            
            if (status.Contains("Live", StringComparison.OrdinalIgnoreCase) || 
                status.Contains("Forecast", StringComparison.OrdinalIgnoreCase)) 
                return "status--live";

            if (status.Contains("HTTP", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                return "status--error";
            }

            return "status--muted";
        }
    }
}
