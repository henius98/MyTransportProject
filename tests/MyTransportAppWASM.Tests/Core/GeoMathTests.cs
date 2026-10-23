using MyTransportAppWASM.Utils;

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
}
