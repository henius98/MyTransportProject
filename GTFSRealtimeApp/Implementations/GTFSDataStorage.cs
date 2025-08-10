using Dapper;
using GTFSRealtimeApp.Interfaces;
using GTFSRealtimeAppSettings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using TransitRealtime;

namespace GTFSRealtimeApp.Implementations
{
    public class GTFSDataStorage : IGTFSDataStorage
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
    }
}
