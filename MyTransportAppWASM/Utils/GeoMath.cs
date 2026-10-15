namespace MyTransportAppWASM.Utils;

public static class GeoMath
{
  private const double EarthRadiusKm = 6371.0;

  public static double DistanceKilometers(double lat1, double lon1, double lat2, double lon2)
  {
    var dLat = double.DegreesToRadians(lat2 - lat1);
    var dLon = double.DegreesToRadians(lon2 - lon1);
    var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(double.DegreesToRadians(lat1)) * Math.Cos(double.DegreesToRadians(lat2)) *
            Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
    return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
  }

  public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2) =>
    DistanceKilometers(lat1, lon1, lat2, lon2) * 1000.0;
}
