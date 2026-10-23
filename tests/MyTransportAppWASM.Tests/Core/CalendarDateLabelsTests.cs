using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class CalendarDateLabelsTests
{
    [Theory]
    [InlineData(2026, 8, 1, "十九")]
    [InlineData(2026, 8, 12, "三十")]
    [InlineData(2026, 8, 13, "七月")]
    [InlineData(2026, 8, 31, "十九")]
    [InlineData(2026, 2, 17, "正月")]
    [InlineData(2025, 7, 25, "闰六月")]
    [InlineData(2025, 8, 23, "七月")]
    public void LunarLabelsHandleMonthBoundariesAndLeapMonths(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, CalendarDateLabels.LunarDay(new(year, month, day)));
    }

    [Fact]
    public void MonthHeadingMatchesAugustReference()
    {
        Assert.Equal("农历六月 ～ 七月", CalendarDateLabels.LunarMonthRange(new(2026, 8, 1)));
    }

    [Theory]
    [InlineData(2026, 8, 7, "立秋")]
    [InlineData(2026, 8, 23, "处暑")]
    [InlineData(2026, 9, 23, "秋分")]
    [InlineData(2026, 8, 13, "七月")]
    [InlineData(2026, 8, 8, "廿六")]
    [InlineData(1901, 1, 6, "小寒")]
    [InlineData(2100, 12, 22, "冬至")]
    public void SolarTermsReplaceLunarDayOnlyOnThePublishedDates(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, CalendarDateLabels.SecondaryDateLabel(new(year, month, day)));
    }

    [Theory]
    [InlineData(2026, 8, 2026, 7, 27, 6)]
    [InlineData(2026, 2, 2026, 1, 26, 5)]
    [InlineData(2021, 2, 2021, 2, 1, 4)]
    [InlineData(2025, 1, 2024, 12, 30, 5)]
    [InlineData(2024, 2, 2024, 1, 29, 5)]
    public void MonthGridUsesCompleteMondayFirstWeeks(int year, int month, int startYear, int startMonth, int startDay, int weeks)
    {
        Assert.Equal(new DateTime(startYear, startMonth, startDay), CalendarDateLabels.MonthGridStart(new(year, month, 1)));
        Assert.Equal(weeks, CalendarDateLabels.MonthWeekCount(new(year, month, 1)));
    }

    [Theory]
    [InlineData(1900, 1, 1)]
    [InlineData(2102, 1, 1)]
    public void UnsupportedLunarDatesLeaveTheGregorianCalendarUsable(int year, int month, int day)
    {
        Assert.Empty(CalendarDateLabels.LunarDay(new(year, month, day)));
        Assert.Empty(CalendarDateLabels.LunarMonthRange(new(year, month, day)));
    }
}
