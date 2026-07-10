using Microsoft.JSInterop;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services;

public class ThemeService
{
  private readonly IJSRuntime _js;
  private readonly IUserSettingsService _userSettingsService;
  private bool _isDarkMode = true;

  public event Action? OnThemeChanged;

  public ThemeService(IJSRuntime js, IUserSettingsService userSettingsService)
  {
    _js = js;
    _userSettingsService = userSettingsService;
  }

  public bool IsDarkMode
  {
    get => _isDarkMode;
    private set
    {
      if (_isDarkMode != value)
      {
        _isDarkMode = value;
        OnThemeChanged?.Invoke();
      }
    }
  }

  public async Task InitializeAsync()
  {
      var settings = await _userSettingsService.GetSettingsAsync();
      IsDarkMode = settings.Theme != "light";
      await _js.InvokeVoidAsync("themeManager.setTheme", IsDarkMode, "global");
  }

  public async Task ToggleThemeAsync()
  {
    IsDarkMode = !IsDarkMode;
    await _js.InvokeVoidAsync("themeManager.setTheme", IsDarkMode, "global");
    await _userSettingsService.UpdateThemeAsync(IsDarkMode ? "dark" : "light");
  }
}
