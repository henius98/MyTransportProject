using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Services
{
  public class LiveRoutingService : ILiveRoutingService
  {
    public List<EnrichedRoute> EnrichRoutes(List<GoogleRoute> googleRoutes, List<BusLocation> liveVehicles)
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

        for (int stepIndex = 0; stepIndex < route.TransitSteps.Count; stepIndex++)
        {
          var step = route.TransitSteps[stepIndex];
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
          double closestDistance = double.MaxValue;
          foreach (var bus in matchingBuses)
          {
            var dist = GeoMath.DistanceMeters(bus.Lat, bus.Lng, step.DepartureLat, step.DepartureLng);
            if (dist < closestDistance)
            {
              closestDistance = dist;
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
          // Note: scoped to the first transit step as subsequent connections are modeled by the base route.
          if (stepIndex == 0)
          {
            enriched.LiveTotalDurationSeconds += estimatedSeconds;
          }
        }

        result.Add(enriched);
      }

      // Rank the routes: those with live buses and lowest live duration first, then those without live buses
      return result
        .OrderByDescending(r => r.HasLiveBus)
        .ThenBy(r => r.LiveTotalDurationSeconds)
        .ToList();
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
  }
}
