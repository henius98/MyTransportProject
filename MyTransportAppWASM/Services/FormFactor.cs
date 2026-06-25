
namespace MyTransportAppWASM.Services
{
  public class FormFactor : IFormFactor
  {
    public string GetFormFactor()
    {
      return "WebAssembly";
    }

    public string GetPlatform()
    {
      return "Blazor WebAssembly";
    }
  }
}
