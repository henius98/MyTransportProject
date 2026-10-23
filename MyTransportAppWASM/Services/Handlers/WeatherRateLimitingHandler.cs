using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyTransportAppWASM.Services.Handlers
{
    public sealed class WeatherRateLimitingHandler : DelegatingHandler
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
                ref var nextRequest = ref CollectionsMarshal.GetValueRefOrAddDefault(_nextRequestByHost, host, out _);
                var scheduledRequest = Math.Max(now, nextRequest);
                delay = Stopwatch.GetElapsedTime(now, scheduledRequest);
                nextRequest = scheduledRequest + RequestIntervalTicks;
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
#pragma warning disable CA1416 // Executed exclusively by host test runner
            _scheduleLock.Wait();
#pragma warning restore CA1416
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
