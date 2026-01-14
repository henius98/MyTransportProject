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

        private static readonly Dictionary<string, string[]> TablePrimaryKeys =
    new(StringComparer.OrdinalIgnoreCase)
    {
        ["trips"] = new[] { "trip_id" },
        ["calendar"] = new[] { "service_id", "start_date", "end_date" },
        ["routes"] = new[] { "route_id" },
        ["shapes"] = new[] { "shape_id", "shape_pt_sequence" },
        ["stops"] = new[] { "stop_id" },
        ["stop_times"] = new[] { "trip_id", "stop_sequence" }
    };

        private static bool TryGetUpsertInfo(
    string tableName,
    string[] headers,
    out string[] pkColumns,
    out string[] updatableColumns)
        {
            // default out values
            pkColumns = Array.Empty<string>();
            updatableColumns = Array.Empty<string>();

            if (!TablePrimaryKeys.TryGetValue(tableName, out var pkFromMap))
            {
                return false;
            }

            var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);

            // Use locals so we don't capture out params inside lambdas
            var pkCols = pkFromMap
                .Where(h => headerSet.Contains(h))
                .ToArray();

            if (pkCols.Length == 0)
            {
                return false;
            }

            // Build a set from the local PK list
            var pkSet = new HashSet<string>(pkCols, StringComparer.OrdinalIgnoreCase);

            var updateCols = headers
                .Where(h => !pkSet.Contains(h))
                .ToArray();

            // Assign to out params only after all LINQ is done
            pkColumns = pkCols;
            updatableColumns = updateCols;

            return true;
        }

        private static void AppendOnConflictClause(
            StringBuilder sb,
            bool hasUpsertInfo,
            string[] pkColumns,
            string[] updatableColumns)
        {
            if (hasUpsertInfo && pkColumns.Length > 0 && updatableColumns.Length > 0)
            {
                var conflictTarget = string.Join(",", pkColumns.Select(c => $"\"{c}\""));
                var setClause = string.Join(",", updatableColumns.Select(c => $"\"{c}\" = excluded.\"{c}\""));
                sb.Append($" ON CONFLICT({conflictTarget}) DO UPDATE SET {setClause};");
            }
            else if (hasUpsertInfo && pkColumns.Length > 0)
            {
                // We know the PK, but there is nothing to update => just ignore duplicates
                var conflictTarget = string.Join(",", pkColumns.Select(c => $"\"{c}\""));
                sb.Append($" ON CONFLICT({conflictTarget}) DO NOTHING;");
            }
            else
            {
                // No PK metadata => plain insert
                sb.Append(";");
            }
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
                // DO NOT dispose the injected _connection (it is shared).
                var conn = _connection;

                if (conn.State != ConnectionState.Open)
                {
                    conn.Open();
                }

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

                // Figure out PK + updatable columns for UPSERT
                bool hasUpsertInfo = TryGetUpsertInfo(tableName, headers, out var pkColumns, out var updatableColumns);

                using var tx = conn.BeginTransaction();

                int rowCount = 0;

                // You had 999999; that risks gigantic SQL strings. 1–5k is usually enough.
                const int batchSize = 3000;

                var sb = new StringBuilder();

                // Reused insert prefix
                string columnList = string.Join(",", headers.Select(h => $"\"{h}\""));
                string insertPrefix = $"INSERT INTO \"{tableName}\" ({columnList}) VALUES ";

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
                            // Flush previous batch with ON CONFLICT
                            AppendOnConflictClause(sb, hasUpsertInfo, pkColumns, updatableColumns);

                            using var insertCmd = conn.CreateCommand();
                            insertCmd.Transaction = tx;
                            insertCmd.CommandText = sb.ToString();
                            insertCmd.ExecuteNonQuery();

                            sb.Clear();
                        }

                        sb.Append(insertPrefix);
                    }
                    else
                    {
                        sb.Append(",");
                    }

                    // Escape values
                    var values = fields.Select(f =>
                    {
                        if (f is null)
                            return "''";

                        // basic SQL escaping for single quotes
                        var escaped = f.Replace("'", "''");
                        return $"'{escaped}'";
                    });

                    sb.Append("(" + string.Join(",", values) + ")");

                    rowCount++;
                }

                // Flush last batch
                if (sb.Length > 0)
                {
                    AppendOnConflictClause(sb, hasUpsertInfo, pkColumns, updatableColumns);

                    using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = sb.ToString();
                    insertCmd.ExecuteNonQuery();
                }

                tx.Commit();
                _logger.LogInformation("Upserted {RowCount} rows into {TableName}", rowCount, tableName);
            }
            finally
            {
                fileStream.Dispose();

                // Optional: keep the same pattern as SaveDataAsync and close after work
                if (_connection.State == ConnectionState.Open)
                {
                    _connection.Close();
                }
            }

            return Task.CompletedTask;
        }

    }
}
