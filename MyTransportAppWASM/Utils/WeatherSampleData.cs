using MyTransportAppWASM.Models.Weather;

namespace MyTransportAppWASM.Utils
{
    public static class WeatherSampleData
    {
        public static IReadOnlyList<WeatherTimeSlice> SampleBaseline(WeatherQuery query)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new List<WeatherTimeSlice>
            {
                new()
                {
                    Label = "Daytime",
                    Time = now,
                    TemperatureC = 30,
                    PrecipitationMm = 1.2,
                    ProbabilityOfRain = 0.35,
                    Summary = "Scattered showers."
                }
            };
        }

        public static IReadOnlyList<WeatherTimeSlice> SampleRadar(WeatherQuery query)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new List<WeatherTimeSlice>
            {
                new()
                {
                    Label = "Next 60 minutes",
                    Time = now,
                    PrecipitationMm = 0,
                    ProbabilityOfRain = 0.1,
                    Summary = "Clear radar scan."
                }
            };
        }

        public static IReadOnlyList<WeatherTimeSlice> SampleOutlook(WeatherQuery query)
        {
            DateTimeOffset today = DateTimeOffset.UtcNow;
            return new List<WeatherTimeSlice>
            {
                new()
                {
                    Label = "Sample Outlook",
                    Time = today,
                    TemperatureC = 31,
                    ProbabilityOfRain = 0.4,
                    Summary = "Isolated storms possible."
                }
            };
        }
    }
}
