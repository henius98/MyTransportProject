using Microsoft.JSInterop;

namespace MyTransportAppWASM.Core.Services;

public sealed class ThemeService(IJSRuntime js, IUserSettingsService userSettingsService)
{
  public event Action? OnThemeChanged;

  public bool IsDarkMode
  {
    get => field;
    private set
    {
      if (field != value)
      {
        field = value;
        OnThemeChanged?.Invoke();
      }
    }
  } = true;

  public async Task ApplyAsync(UserSettings settings)
  {
    IsDarkMode = settings.Theme != "light";
    await js.InvokeVoidAsync("themeManager.setTheme", IsDarkMode);
  }

  public async Task ToggleThemeAsync()
  {
    IsDarkMode = !IsDarkMode;
    await js.InvokeVoidAsync("themeManager.setTheme", IsDarkMode);
    await userSettingsService.UpdateThemeAsync(IsDarkMode ? "dark" : "light");
  }
}
