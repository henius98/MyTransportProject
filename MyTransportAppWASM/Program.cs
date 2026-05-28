using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Models.Weather;

namespace MyTransportAppWASM
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Automatically loads appsettings.json and appsettings.{Environment}.json from wwwroot.
            // The framework fetches these via HTTP and merges them into builder.Configuration.
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");

            builder.Services.Configure<WeatherOptions>(builder.Configuration.GetSection("WeatherProviders"));
            builder.Services.AddMemoryCache();

            builder.Services.AddSingleton<IFormFactor, FormFactor>();
            
            // Shared Resilience Policy
            var retryPolicy = Polly.Extensions.Http.HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

            builder.Services.AddHttpClient<IGtfsService, GtfsService>()
                .AddPolicyHandler(retryPolicy);

            builder.Services.AddHttpClient<IWeatherPlannerService, WeatherPlannerService>()
                .AddPolicyHandler(retryPolicy);

            builder.Services.AddHttpClient<IGeocodingService, GeocodingService>()
                .AddPolicyHandler(retryPolicy);

            builder.Services.AddScoped<ThemeService>();
            builder.Services.AddScoped<ILocationService, LocationService>();

            builder.Services.AddOidcAuthentication(options =>
            {
                builder.Configuration.Bind("Authentication:Google", options.ProviderOptions);
                options.ProviderOptions.DefaultScopes.Add("openid");
                options.ProviderOptions.DefaultScopes.Add("profile");
                options.ProviderOptions.DefaultScopes.Add("email");
            });

            builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

            await builder.Build().RunAsync();
        }
    }
}
