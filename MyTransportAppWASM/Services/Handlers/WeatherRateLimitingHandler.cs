using System.Diagnostics;

namespace MyTransportAppWASM.Services.Handlers
{
    public class WeatherRateLimitingHandler : DelegatingHandler
    {
        private static readonly long RequestIntervalTicks = Stopwatch.Frequency / 2;
        private readonly SemaphoreSlim _scheduleLock = new(1, 1);
        private readonly Dictionary<string, long> _nextRequestByHost = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? "unknown";
            long delayTicks;

            await _scheduleLock.WaitAsync(cancellationToken);
            try
            {
                var now = Stopwatch.GetTimestamp();
                _nextRequestByHost.TryGetValue(host, out var nextRequest);
                var scheduledRequest = Math.Max(now, nextRequest);
                delayTicks = scheduledRequest - now;
                _nextRequestByHost[host] = scheduledRequest + RequestIntervalTicks;
            }
            finally
            {
                _scheduleLock.Release();
            }

            if (delayTicks > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds((double)delayTicks / Stopwatch.Frequency), cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _scheduleLock.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
