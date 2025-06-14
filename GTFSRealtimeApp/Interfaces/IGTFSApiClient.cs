using GTFSRealtimeAppSettings;
using Models;

namespace GTFSRealtimeApp.Interfaces
{
    public interface IGTFSApiClient
    {
        Task<string> GetDataAsync(string url, CancellationToken cancellationToken = default);
    }
}
