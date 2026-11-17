
namespace MyTransportAppWASM.Tests;

public class GeoMathTests
{
  [Fact]
  public void DistanceKilometers_IdenticalPoints_ReturnsZero()
  {
    var dist = GeoMath.DistanceKilometers(3.139, 101.6869, 3.139, 101.6869);
    Assert.Equal(0, dist, precision: 5);
  }

  [Fact]
  public void DistanceMeters_IdenticalPoints_ReturnsZero()
  {
    var dist = GeoMath.DistanceMeters(3.139, 101.6869, 3.139, 101.6869);
    Assert.Equal(0, dist, precision: 5);
  }

  [Fact]
  public void DistanceKilometers_KLLocations_CalculatesExpectedDistance()
  {
    // KL Sentral (3.1343, 101.6865) to KLCC (3.1578, 101.7119) ~ 3.7 - 3.8 km
    var dist = GeoMath.DistanceKilometers(3.1343, 101.6865, 3.1578, 101.7119);
    Assert.InRange(dist, 3.5, 4.0);
  }

  [Fact]
  public void DistanceMeters_ConsistentWithKilometers()
  {
    var distKm = GeoMath.DistanceKilometers(3.1343, 101.6865, 3.1578, 101.7119);
    var distMeters = GeoMath.DistanceMeters(3.1343, 101.6865, 3.1578, 101.7119);

    Assert.Equal(distKm * 1000.0, distMeters, tolerance: 0.1);
  }

  [Theory]
  [InlineData(0, 0, true)]
  [InlineData(-90, -180, true)]
  [InlineData(90, 180, true)]
  [InlineData(90.1, 0, false)]
  [InlineData(-90.1, 0, false)]
  [InlineData(0, 180.1, false)]
  [InlineData(0, -180.1, false)]
  [InlineData(double.NaN, 101.0, false)]
  [InlineData(3.1, double.PositiveInfinity, false)]
  public void IsValidCoordinates_ValidatesRangeAndFiniteness(double latitude, double longitude, bool expected)
  {
    Assert.Equal(expected, GeoMath.IsValidCoordinates(latitude, longitude));
  }
}
