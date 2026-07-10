namespace MyTransportAppWASM.Models.BaziFlow
{
    public class ApiResponse<T>
    {
        public string Status { get; set; } = string.Empty;
        public T? Data { get; set; }
    }

    public class ApiError
    {
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class CreateProfileRequest
    {
        public int Gender { get; set; }
        public string BirthDate { get; set; } = string.Empty;
        public int BirthHour { get; set; }
        public int BirthMinute { get; set; }
        public string? Location { get; set; }
    }

    public class DateFortuneRequest
    {
        public string Date { get; set; } = string.Empty;
    }

    public class ProfileData
    {
        public ProfileDetail? Profile { get; set; }
    }

    public class ProfileDetail
    {
        public string? Gender { get; set; }
        public string? SolarDate { get; set; }
        public string? LunarDate { get; set; }
        public string? BaziAnalysis { get; set; }
        public string? BaziSummary { get; set; }
    }

    public class FortuneData
    {
        public string Almanac { get; set; } = string.Empty;
        public string Analysis { get; set; } = string.Empty;
    }
}
