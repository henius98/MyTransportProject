using GTFSRealtimeApp.Implementations;
using GTFSRealtimeApp.Interfaces;
using GTFSRealtimeApp.Services;
using GTFSRealtimeAppSettings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace GTFSRealtimeApp
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();

            try
            {
                var logger = host.Services.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("=== GTFS Realtime Console Application Starting ===");

                // Run the host (this will start all background services)
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                var logger = host.Services.GetService<ILogger<Program>>();
                logger?.LogCritical(ex, "Application terminated unexpectedly");
                throw;
            }
            finally
            {
                var logger = host.Services.GetService<ILogger<Program>>();
                logger?.LogInformation("=== GTFS Realtime Console Application Stopped ===");
            }
        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    var env = context.HostingEnvironment.EnvironmentName;

                    // Clear default configuration sources to have full control
                    config.Sources.Clear();

                    config.SetBasePath(Directory.GetCurrentDirectory())
                          .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                          .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false)
                          .AddEnvironmentVariables()
                          .AddCommandLine(args);
                })
                .ConfigureServices((context, services) =>
                {
                    // Configuration binding with validation
                    services.Configure<GTFSRealtimeApiSettings>(
                        context.Configuration.GetSection("GTFS_RealtimeAPI"))
                        .PostConfigure<GTFSRealtimeApiSettings>(ValidateSettings);

                    services.Configure<AppSettings>(
                        context.Configuration.GetSection("AppSettings"));

                    // Register HTTP clients
                    services.AddHttpClient<IGTFSApiClient, GTFSApiClient>(client =>
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                        client.DefaultRequestHeaders.Add("User-Agent", "GTFS-Realtime-Console-App/1.0");
                    })
                    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
                    {
                        AutomaticDecompression = DecompressionMethods.Brotli
                    });

                    // Register application services
                    services.AddSingleton<IGTFSDataProcessor, GTFSDataProcessor>();
                    services.AddSingleton<IGTFSDataStorage, GTFSDataStorage>();
                    services.AddSingleton<IHealthMonitor, HealthMonitor>();

                    // Register background services
                    services.AddHostedService<GTFSBackgroundService>();
                    services.AddHostedService<HealthMonitorService>();
                    services.AddHostedService<ConfigurationMonitorService>();
                })
                .ConfigureLogging((context, logging) =>
                {
                    logging.ClearProviders();
                    logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                    logging.AddSimpleConsole(formatterOptions =>
                    {
                        formatterOptions.IncludeScopes = context.Configuration.GetValue<bool>("Logging:IncludeScopes");
                        formatterOptions.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
                    });
                    logging.AddDebug();

                    // Add file logging if configured
                    var logPath = context.Configuration["Logging:FilePath"];
                    if (!string.IsNullOrEmpty(logPath))
                    {
                        // You would need to add a file logging provider here
                        // logging.AddFile(logPath);
                    }
                })
                .UseConsoleLifetime(options =>
                {
                    options.SuppressStatusMessages = false;
                });

        private static void ValidateSettings(GTFSRealtimeApiSettings settings)
        {
            if (settings?.Prasarana == null)
                throw new InvalidOperationException("Prasarana configuration section is missing");

            if (string.IsNullOrWhiteSpace(settings.Prasarana.RapidKL))
                throw new InvalidOperationException("RapidKL API URL is required");

            if (string.IsNullOrWhiteSpace(settings.Prasarana.RapidPenang))
                throw new InvalidOperationException("RapidPenang API URL is required");

            // Validate URLs
            if (!Uri.TryCreate(settings.Prasarana.RapidKL, UriKind.Absolute, out _))
                throw new InvalidOperationException("RapidKL API URL is not valid");

            if (!Uri.TryCreate(settings.Prasarana.RapidPenang, UriKind.Absolute, out _))
                throw new InvalidOperationException("RapidPenang API URL is not valid");
        }
    }
}
