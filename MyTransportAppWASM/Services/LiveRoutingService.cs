using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
  public class LiveRoutingService : ILiveRoutingService
  {
    public Task<List<EnrichedRoute>> EnrichRoutesAsync(List<GoogleRoute> googleRoutes, List<BusLocation> liveVehicles)
    {
      ArgumentNullException.ThrowIfNull(googleRoutes);
      ArgumentNullException.ThrowIfNull(liveVehicles);

      var vehiclesByRoute = IndexVehiclesByRoute(liveVehicles);
      var result = new List<EnrichedRoute>(googleRoutes.Count);

      foreach (var route in googleRoutes)
      {
        var enriched = new EnrichedRoute
        {
          OriginalRoute = route,
          LiveTotalDurationSeconds = route.DurationSeconds,
          HasLiveBus = false
        };

        foreach (var step in route.TransitSteps)
        {
          if (string.IsNullOrWhiteSpace(step.LineShortName))
          {
            continue;
          }

          var normalizedLineName = NormalizeRouteName(step.LineShortName);
          if (normalizedLineName.Length == 0 ||
              !vehiclesByRoute.TryGetValue(normalizedLineName, out var matchingBuses))
          {
            continue;
          }

          BusLocation? closestBus = null;
          var closestDistance = double.MaxValue;

          foreach (var bus in matchingBuses)
          {
            var distance = CalculateDistance(bus.Lat, bus.Lng, step.DepartureLat, step.DepartureLng);
            if (distance < closestDistance)
            {
              closestDistance = distance;
              closestBus = bus;
            }
          }

          if (closestBus is null)
          {
            continue;
          }

          // Rough ETA: assume 20 km/h average speed (approx 5.5 m/s) in city if straight line.
          int estimatedSeconds = (int)(closestDistance / 5.5);

          enriched.NearestBuses.Add(new LiveBusInfo
          {
            VehicleId = closestBus.VehicleId ?? "",
            RouteId = closestBus.RouteId ?? "",
            Lat = closestBus.Lat,
            Lng = closestBus.Lng,
            DistanceToStopMeters = closestDistance,
            EstimatedArrivalSeconds = estimatedSeconds
          });
          enriched.HasLiveBus = true;

          // Preserve the existing behavior of adding each matched step's wait estimate.
          enriched.LiveTotalDurationSeconds += estimatedSeconds;
        }

        result.Add(enriched);
      }

      // Rank the routes: those with live buses and lowest live duration first, then those without live buses
      var rankedResult = result
        .OrderByDescending(r => r.HasLiveBus)
        .ThenBy(r => r.LiveTotalDurationSeconds)
        .ToList();

      return Task.FromResult(rankedResult);
    }

    private static Dictionary<string, List<BusLocation>> IndexVehiclesByRoute(List<BusLocation> liveVehicles)
    {
      var vehiclesByRoute = new Dictionary<string, List<BusLocation>>(StringComparer.OrdinalIgnoreCase);
      var normalizedRouteNames = new Dictionary<string, string>(StringComparer.Ordinal);

      foreach (var vehicle in liveVehicles)
      {
        var routeId = vehicle.RouteId;
        if (string.IsNullOrWhiteSpace(routeId))
        {
          continue;
        }

        if (!normalizedRouteNames.TryGetValue(routeId, out var normalizedRouteName))
        {
          normalizedRouteName = NormalizeRouteName(routeId);
          normalizedRouteNames.Add(routeId, normalizedRouteName);
        }

        if (normalizedRouteName.Length == 0)
        {
          continue;
        }

        if (!vehiclesByRoute.TryGetValue(normalizedRouteName, out var routeVehicles))
        {
          routeVehicles = new List<BusLocation>();
          vehiclesByRoute.Add(normalizedRouteName, routeVehicles);
        }

        routeVehicles.Add(vehicle);
      }

      return vehiclesByRoute;
    }

    private static string NormalizeRouteName(string routeName)
    {
      var normalizedLength = 0;
      var isAlreadyNormalized = true;

      foreach (var character in routeName)
      {
        if (char.IsLetterOrDigit(character))
        {
          normalizedLength++;
        }
        else
        {
          isAlreadyNormalized = false;
        }
      }

      if (normalizedLength == 0)
      {
        return string.Empty;
      }

      if (isAlreadyNormalized)
      {
        return routeName;
      }

      return string.Create(normalizedLength, routeName, static (destination, source) =>
      {
        var destinationIndex = 0;
        foreach (var character in source)
        {
          if (char.IsLetterOrDigit(character))
          {
            destination[destinationIndex++] = character;
          }
        }
      });
    }

    private static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
      const double earthRadiusMeters = 6_371_000;
      var lat1Radians = ToRadians(lat1);
      var lat2Radians = ToRadians(lat2);
      var dLat = lat2Radians - lat1Radians;
      var dLon = ToRadians(lon2 - lon1);
      var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
              Math.Cos(lat1Radians) * Math.Cos(lat2Radians) *
              Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
      return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * (Math.PI / 180);
  }
}
