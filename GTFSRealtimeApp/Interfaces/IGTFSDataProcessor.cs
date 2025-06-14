using GTFSRealtimeAppSettings;
using Models;

namespace GTFSRealtimeApp.Interfaces
{
    public interface IGTFSDataProcessor
    {
        Task<JobResult> ProcessAllDataAsync(GTFSRealtimeApiSettings settings, CancellationToken cancellationToken);
    }
}
