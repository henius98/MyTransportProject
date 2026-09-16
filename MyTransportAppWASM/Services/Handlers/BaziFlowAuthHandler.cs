using System.Net.Http.Headers;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services.Handlers
{
    public class BaziFlowAuthHandler : DelegatingHandler
    {
        private readonly IBrowserStorageService _localStorage;
        private const string ApiKeyStorageKey = "baziflow_api_key";

        public BaziFlowAuthHandler(IBrowserStorageService localStorage)
        {
            _localStorage = localStorage;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var apiKey = await _localStorage.GetStringAsync(ApiKeyStorageKey, cancellationToken);
            
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
