
namespace MyTransportAppWASM.Features.Google.Services
{
  public interface ILiveRoutingService
  {
    List<EnrichedRoute> EnrichRoutes(List<GoogleRoute> googleRoutes, List<BusLocation> liveVehicles);
  }
}
