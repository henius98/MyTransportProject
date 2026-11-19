
namespace MyTransportAppWASM.Features.Transit.Services;

public interface ITransportStaticDataService
{
  Task<List<StaticRoute>> GetRoutesAsync(TransportStaticData source, CancellationToken cancellationToken = default);
  Task<string> GetMapAsync(TransportStaticData source, string routeId = "", CancellationToken cancellationToken = default);
  Task<List<StaticDeparture>> GetDeparturesAsync(TransportStaticData source, string routeId, string stopId, CancellationToken cancellationToken = default);
}
