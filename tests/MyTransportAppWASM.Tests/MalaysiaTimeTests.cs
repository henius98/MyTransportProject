using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class MalaysiaTimeTests
{
    [Fact]
    public void At_AssignsMalaysiaOffsetWithoutChangingWallClockTime()
    {
        var result = MalaysiaTime.At(new DateTime(2026, 9, 23, 0, 0, 0));

        Assert.Equal(new DateTimeOffset(2026, 9, 23, 0, 0, 0, MalaysiaTime.Offset), result);
    }

    [Fact]
    public void Convert_ConvertsUtcInstantToMalaysiaTime()
    {
        var utc = new DateTimeOffset(2026, 9, 22, 16, 0, 0, TimeSpan.Zero);

        var result = MalaysiaTime.Convert(utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 23, 0, 0, 0, MalaysiaTime.Offset), result);
    }
}
