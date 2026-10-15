using System.Diagnostics;

namespace MyTransportAppWASM.Services.Handlers
{
    public class WeatherRateLimitingHandler : DelegatingHandler
    {
        private static readonly long RequestIntervalTicks = Stopwatch.Frequency / 2;
        private static readonly SemaphoreSlim _scheduleLock = new(1, 1);
        private static readonly Dictionary<string, long> _nextRequestByHost = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? "unknown";
            TimeSpan delay;

            await _scheduleLock.WaitAsync(cancellationToken);
            try
            {
                var now = Stopwatch.GetTimestamp();
                _nextRequestByHost.TryGetValue(host, out var nextRequest);
                var scheduledRequest = Math.Max(now, nextRequest);
                delay = Stopwatch.GetElapsedTime(now, scheduledRequest);
                _nextRequestByHost[host] = scheduledRequest + RequestIntervalTicks;
            }
            finally
            {
                _scheduleLock.Release();
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }

        public static void ResetForTesting()
        {
            _scheduleLock.WaitAsync().GetAwaiter().GetResult();
            try
            {
                _nextRequestByHost.Clear();
            }
            finally
            {
                _scheduleLock.Release();
            }
        }
    }
}
