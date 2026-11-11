using Microsoft.JSInterop;

namespace MyTransportAppWASM.Core.Services;

public sealed class ThemeService
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

  public async Task ApplyAsync(UserSettings settings)
  {
      IsDarkMode = settings.Theme != "light";
      await _js.InvokeVoidAsync("themeManager.setTheme", IsDarkMode);
  }

  public async Task ToggleThemeAsync()
  {
    IsDarkMode = !IsDarkMode;
    await _js.InvokeVoidAsync("themeManager.setTheme", IsDarkMode);
    await _userSettingsService.UpdateThemeAsync(IsDarkMode ? "dark" : "light");
  }
}
