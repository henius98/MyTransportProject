namespace MyTransportAppWASM.Models.BaziFlow
{
    public record ApiResponse<T>
    {
        public string Status { get; set; } = string.Empty;
        public T? Data { get; set; }
    }

    public record ApiError
    {
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public record CreateProfileRequest
    {
        public int Gender { get; set; }
        public string BirthDate { get; set; } = string.Empty;
        public int BirthHour { get; set; }
        public int BirthMinute { get; set; }
        public string? Location { get; set; }
    }

    public record DateFortuneRequest
    {
        public string Date { get; set; } = string.Empty;
    }

    public record ProfileData
    {
        public ProfileDetail? Profile { get; set; }
    }

    public record ProfileDetail
    {
        public string? Gender { get; set; }
        public string? SolarDate { get; set; }
        public string? LunarDate { get; set; }
        public string? BaziAnalysis { get; set; }
        public string? BaziSummary { get; set; }
    }

    public record FortuneData
    {
        public string Almanac { get; set; } = string.Empty;
        public string Analysis { get; set; } = string.Empty;
        public List<string> FavorableDirections { get; set; } = new();
        public List<int> LuckyHours { get; set; } = new();
    }
}
