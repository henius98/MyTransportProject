namespace MyTransportAppWASM.Tests;

public class WeatherStatusBadgeTests
{
  [Theory]
  [InlineData(WeatherBadgeTone.Muted, "status--muted")]
  [InlineData(WeatherBadgeTone.Live, "status--live")]
  [InlineData(WeatherBadgeTone.Error, "status--error")]
  public void GetStatusClass_ReturnsExpectedCssClass(WeatherBadgeTone tone, string expected)
  {
    Assert.Equal(expected, WeatherStatusBadge.GetStatusClass(tone));
  }
}
