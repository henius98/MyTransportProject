using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace MyTransportAppWASM.BrowserBenchmarks;

public static class Program
{
  public static async Task Main(string[] args)
  {
    var builder = WebAssemblyHostBuilder.CreateDefault(args);
    builder.RootComponents.Add<BenchmarkApp>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");
    await builder.Build().RunAsync();
  }
}
