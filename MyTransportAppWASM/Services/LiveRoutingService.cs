using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
  public class LiveRoutingService : ILiveRoutingService
  {
    public Task<List<EnrichedRoute>> EnrichRoutesAsync(List<GoogleRoute> googleRoutes, List<BusLocation> liveVehicles)
    {
      var result = new List<EnrichedRoute>();

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
          // Find matching buses for this line
          var matchingBuses = liveVehicles.Where(v => 
            !string.IsNullOrEmpty(v.RouteId) && 
            (v.RouteId.Contains(step.LineShortName, StringComparison.OrdinalIgnoreCase) || 
             step.LineShortName.Contains(v.RouteId, StringComparison.OrdinalIgnoreCase))
          ).ToList();

          if (matchingBuses.Any())
          {
            // Find the closest bus to the departure stop
            var closestBus = matchingBuses
              .Select(b => new { 
                Bus = b, 
                Dist = CalculateDistance(b.Lat, b.Lng, step.DepartureLat, step.DepartureLng) 
              })
              .OrderBy(x => x.Dist)
              .First();

            // Rough ETA: assume 20 km/h average speed (approx 5.5 m/s) in city if straight line
            int estimatedSeconds = (int)(closestBus.Dist / 5.5);

            enriched.NearestBuses.Add(new LiveBusInfo
            {
              VehicleId = closestBus.Bus.VehicleId ?? "",
              RouteId = closestBus.Bus.RouteId ?? "",
              Lat = closestBus.Bus.Lat,
              Lng = closestBus.Bus.Lng,
              DistanceToStopMeters = closestBus.Dist,
              EstimatedArrivalSeconds = estimatedSeconds
            });
            enriched.HasLiveBus = true;
            
            // Recompute total duration (this is a simplified logic, adding the wait time for the first transit step)
            // Ideally, we only add wait time for the first leg, but for now we'll add max wait time across steps
            enriched.LiveTotalDurationSeconds += estimatedSeconds;
          }
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

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
      var R = 6371e3; // metres
      var dLat = ToRadians(lat2 - lat1);
      var dLon = ToRadians(lon2 - lon1);
      var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
              Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
              Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
      return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private double ToRadians(double deg) => deg * (Math.PI / 180);
  }
}
