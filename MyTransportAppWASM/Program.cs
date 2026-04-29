using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");

            builder.Services.AddSingleton<IFormFactor, FormFactor>();
            builder.Services.AddHttpClient<IGtfsService, GtfsService>();
            builder.Services.AddHttpClient<IWeatherPlannerService, WeatherPlannerService>();
            builder.Services.AddHttpClient<IGeocodingService, GeocodingService>();
            builder.Services.AddScoped<ThemeService>();

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
