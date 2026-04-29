using TransitRealtime;

namespace MyTransportAppWASM.Services.Interfaces
{
    public interface IGtfsService
    {
        Task<FeedMessage?> GetBusPositionsAsync(string url, CancellationToken cancellationToken = default);
    }
}
