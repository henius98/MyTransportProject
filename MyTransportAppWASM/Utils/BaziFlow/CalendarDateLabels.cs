using System.Globalization;

namespace MyTransportAppWASM.Utils;

public static class CalendarDateLabels
{
    private static readonly ChineseLunisolarCalendar LunarCalendar = new();
    private static readonly string[] Months = ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];
    private static readonly string[] Digits = ["一", "二", "三", "四", "五", "六", "七", "八", "九", "十"];

    public static string SecondaryDateLabel(DateTime date) => SolarTermDates.NameOn(date) ?? LunarDay(date);

    public static DateTime MonthGridStart(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        return first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
    }

    public static int MonthWeekCount(DateTime month) =>
        (new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month)) - MonthGridStart(month)).Days / 7 + 1;

    public static string LunarMonth(DateTime date)
    {
        if (!IsSupported(date)) return string.Empty;
        var month = LunarCalendar.GetMonth(date);
        var leapMonth = LunarCalendar.GetLeapMonth(LunarCalendar.GetYear(date));
        var isLeap = month == leapMonth;
        if (leapMonth > 0 && month >= leapMonth) month--;
        return (isLeap ? "闰" : "") + Months[month - 1];
    }

    public static string LunarDay(DateTime date)
    {
        if (!IsSupported(date)) return string.Empty;
        var day = LunarCalendar.GetDayOfMonth(date);
        return day switch
        {
            1 => LunarMonth(date),
            <= 10 => "初" + Digits[day - 1],
            < 20 => "十" + Digits[day - 11],
            20 => "二十",
            < 30 => "廿" + Digits[day - 21],
            _ => "三十"
        };
    }

    public static string LunarMonthRange(DateTime month)
    {
        var first = LunarMonth(new DateTime(month.Year, month.Month, 1));
        var last = LunarMonth(new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month)));
        if (first.Length == 0 || last.Length == 0) return string.Empty;
        return first == last ? $"农历{first}" : $"农历{first} ～ {last}";
    }

    private static bool IsSupported(DateTime date) => date >= LunarCalendar.MinSupportedDateTime && date <= LunarCalendar.MaxSupportedDateTime;
}
