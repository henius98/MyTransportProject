using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services.Interfaces
{
  public interface ILiveRoutingService
  {
    Task<List<EnrichedRoute>> EnrichRoutesAsync(List<GoogleRoute> googleRoutes, List<BusLocation> liveVehicles);
  }
}
