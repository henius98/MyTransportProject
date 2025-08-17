using GTFSRealtimeAppSettings;
using Models;
using TransitRealtime;

namespace GTFSRealtimeApp.Interfaces
{
    public interface IGTFSApiClient
    {
        Task<FeedMessage> GetDataAsync(string url, CancellationToken cancellationToken = default);
        Task<Stream> GetGtfsStaticDataAsync(string category, CancellationToken ct = default);
    }
}
