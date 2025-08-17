using Dapper;
using GTFSRealtimeApp.Interfaces;
using GTFSRealtimeAppSettings;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic.FileIO;
using System.Data;
using System.Text;
using TransitRealtime;

namespace GTFSRealtimeApp.Implementations
{
    public sealed class GTFSDataStorage : IGTFSDataStorage
    {
        private readonly ILogger<GTFSDataStorage> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;
        private readonly IDbConnection _connection;

        public GTFSDataStorage(ILogger<GTFSDataStorage> logger, IOptionsMonitor<AppSettings> settings, IDbConnection connection)
        {
            _logger = logger;
            _settings = settings;
            _connection = connection;
        }

        public async Task SaveDataAsync(string source, FeedMessage data, CancellationToken cancellationToken = default)
        {
            try
            {
                var sbTrip = new StringBuilder();
                sbTrip.Append("INSERT OR IGNORE INTO Trip (TripId, RouteId, VehicleId) VALUES ");

                var sbPos = new StringBuilder();
                sbPos.Append("INSERT INTO VehiclePositions (TripId, Latitude, Longitude, Bearing, Speed, Timestamp) VALUES ");

                for (int i = 0; i < data.Entity.Count; i++)
                {
                    var r = data.Entity[i].Vehicle;

                    sbTrip.Append($"('{r.Trip.TripId}', '{r.Trip.RouteId}', '{r.Vehicle.Id}')");
                    sbPos.Append($"('{r.Trip.TripId}', {r.Position.Latitude}, {r.Position.Longitude}, {r.Position.Bearing}, {r.Position.Speed}, {r.Timestamp})");

                    if (i < data.Entity.Count - 1)
                    {
                        sbTrip.Append(",");
                        sbPos.Append(",");
                    }
                }
                sbTrip.Append(";");
                sbPos.Append(";");

                _connection.Open();

                using var transaction = _connection.BeginTransaction();
                await _connection.ExecuteAsync(sbTrip.ToString(), transaction: transaction);
                await _connection.ExecuteAsync(sbPos.ToString(), transaction: transaction);
                transaction.Commit();
                _connection.Close();

            }
            catch (Exception ex)
            {

                _logger.LogError(ex, "Failed to save {Source} data", source);
                throw;
            }
        }

        public async Task SaveGtfsStaticDataAsync(Stream httpStream, CancellationToken ct = default)
        {
            var tasks = new List<Task>();
            var semaphore = new SemaphoreSlim(1); // limit parallelism

            try
            {
                using var zipStream = new ZipInputStream(httpStream);
                ZipEntry? entry;

                while ((entry = zipStream.GetNextEntry()) != null)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name) ||
                        !entry.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                        entry.Name.Equals("agency.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _logger.LogInformation("Buffering GTFS file: {FileName}", entry.Name);

                    using var ms = new MemoryStream();
                    await zipStream.CopyToAsync(ms, ct);
                    ms.Position = 0;

                    var localCopy = new MemoryStream(ms.ToArray());
                    var fileName = entry.Name;

                    // throttle with semaphore
                    await semaphore.WaitAsync(ct);
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessGtfsFile(fileName, localCopy, ct);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, ct);

                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save GTFS Static data in parallel (ZipInputStream)");
                throw;
            }
        }


        /// <summary>
        /// Each worker processes one GTFS file from a MemoryStream
        /// </summary>
        private Task ProcessGtfsFile(string fileName, MemoryStream fileStream, CancellationToken ct)
        {
            try
            {
                using var conn = _connection; // scope-managed
                conn.Open();

                using var reader = new StreamReader(fileStream, Encoding.UTF8);
                using var parser = new TextFieldParser(reader)
                {
                    TextFieldType = FieldType.Delimited
                };
                parser.SetDelimiters(",");

                if (parser.EndOfData)
                {
                    return Task.CompletedTask;
                }

                string[]? headers = parser.ReadFields();
                if (headers is null || headers.Length == 0)
                {
                    return Task.CompletedTask;
                }

                string tableName = Path.GetFileNameWithoutExtension(fileName);

                using var tx = conn.BeginTransaction();

                int rowCount = 0;
                const int batchSize = 999999; // adjust based on memory/SQL limits

                var sb = new StringBuilder();

                while (!parser.EndOfData)
                {
                    ct.ThrowIfCancellationRequested();
                    var fields = parser.ReadFields();
                    if (fields == null)
                    {
                        continue;
                    }

                    if (rowCount % batchSize == 0)
                    {
                        if (sb.Length > 0)
                        {
                            // flush previous batch
                            using var insertCmd = conn.CreateCommand();
                            insertCmd.Transaction = tx;
                            insertCmd.CommandText = sb.ToString();
                            insertCmd.ExecuteNonQuery();
                            sb.Clear();
                        }

                        sb.Append($"INSERT INTO \"{tableName}\" ({string.Join(",", headers.Select(h => $"\"{h}\""))}) VALUES ");
                    }
                    else
                    {
                        sb.Append(",");
                    }

                    // escape/quote fields safely
                    var values = fields
                        .Select(f => f == null ? "''" : $"'{f.Replace("'", "''")}'"); // simple SQL escaping
                    sb.Append("(" + string.Join(",", values) + ")");

                    rowCount++;
                }

                // flush last batch
                if (sb.Length > 0)
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = sb.ToString();
                    insertCmd.ExecuteNonQuery();
                }

                tx.Commit();
                _logger.LogInformation("Inserted {RowCount} rows into {TableName}", rowCount, tableName);
            }
            finally
            {
                fileStream.Dispose();
            }
            return Task.CompletedTask;
        }
    }
}
