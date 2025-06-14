using GTFSRealtimeAppSettings;
using Models;

namespace GTFSRealtimeApp.Interfaces
{
    public interface IGTFSDataStorage
    {
        Task SaveDataAsync(string source, string data, CancellationToken cancellationToken = default);
    }
}
