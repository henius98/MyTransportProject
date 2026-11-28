
namespace MyTransportAppWASM.Features.Transit.Services;

public interface ITransportStaticDataService
{
  Task<List<StaticRoute>> GetRoutesAsync(TransportStaticData source, CancellationToken cancellationToken = default);
  Task<string> GetMapAsync(TransportStaticData source, string routeId = "", string? serviceDate = null, CancellationToken cancellationToken = default);
  Task<List<StaticFrequency>> GetFrequenciesAsync(TransportStaticData source, CancellationToken cancellationToken = default);
}
