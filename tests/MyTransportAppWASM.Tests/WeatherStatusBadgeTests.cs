using MyTransportAppWASM.Pages.Weather.Components;

namespace MyTransportAppWASM.Tests;

public class WeatherStatusBadgeTests
{
  [Theory]
  [InlineData(null, "status--muted")]
  [InlineData("", "status--muted")]
  [InlineData("   ", "status--muted")]
  [InlineData("Live radar", "status--live")]
  [InlineData("7-day Forecast", "status--live")]
  [InlineData("Today", "status--live")]
  [InlineData("HTTP Error", "status--error")]
  [InlineData("Unavailable", "status--error")]
  [InlineData("Network Error", "status--error")]
  [InlineData("Error in Live feed", "status--error")]
  [InlineData("HTTP Error: Live Unavailable", "status--error")]
  [InlineData("Forecast Error", "status--error")]
  [InlineData("Normal", "status--muted")]
  public void GetStatusClass_ReturnsExpectedCssClass(string? status, string expected)
  {
    Assert.Equal(expected, WeatherStatusBadge.GetStatusClass(status));
  }
}
