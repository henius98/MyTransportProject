using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services.Handlers
{
    public class BaziFlowAuthHandler : DelegatingHandler
    {
        private readonly IBrowserStorageService _localStorage;
        private readonly string? _expectedHost;
        private const string ApiKeyStorageKey = "baziflow_api_key";

        public BaziFlowAuthHandler(IBrowserStorageService localStorage, IConfiguration? configuration = null)
        {
            _localStorage = localStorage;
            _expectedHost = configuration?["BaziFlow:BaseUrl"] is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.Host
                : null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isTargetHost = _expectedHost is null
                || request.RequestUri is null
                || !request.RequestUri.IsAbsoluteUri
                || string.Equals(request.RequestUri.Host, _expectedHost, StringComparison.OrdinalIgnoreCase);

            if (isTargetHost)
            {
                var apiKey = await _localStorage.GetStringAsync(ApiKeyStorageKey, cancellationToken);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
