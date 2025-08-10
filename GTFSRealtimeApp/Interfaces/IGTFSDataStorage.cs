using TransitRealtime;

namespace GTFSRealtimeApp.Interfaces
{
    public interface IGTFSDataStorage
    {
        Task SaveDataAsync(string source, FeedMessage data, CancellationToken cancellationToken = default);
    }
}
