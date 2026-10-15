using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services.Interfaces;

public interface ITransitTrackingService : IDisposable
{
  IReadOnlyList<BusLocation> CachedVehicles { get; }

  Task<TransitTrackingResult> RefreshVehiclesAsync(
    double latitude,
    double longitude,
    bool force = false,
    CancellationToken cancellationToken = default);

  Task<List<EnrichedRoute>> EnrichRoutesAsync(
    List<GoogleRoute> googleRoutes,
    CancellationToken cancellationToken = default);

  void Reset();
}
