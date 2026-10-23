using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services.Interfaces
{
  public interface ILiveRoutingService
  {
    List<EnrichedRoute> EnrichRoutes(List<GoogleRoute> googleRoutes, List<BusLocation> liveVehicles);
  }
}
