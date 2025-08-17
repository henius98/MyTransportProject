using System.IO.Compression;
using TransitRealtime;

namespace GTFSRealtimeApp.Interfaces
{
    public interface IGTFSDataStorage
    {
        Task SaveDataAsync(string source, FeedMessage data, CancellationToken cancellationToken = default);
        Task SaveGtfsStaticDataAsync(Stream httpStream, CancellationToken ct = default);
    }
}
