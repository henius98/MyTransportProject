using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyTransportAppWASM.Core.Services
{
  public sealed class LanguageOption
  {
    public string Code { get; set; } = string.Empty;
    public string NativeLabel { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
  }

  public sealed class LanguageService(HttpClient http)
  {
    private const string DefaultLanguageResourceName = "MyTransportAppWASM.i18n.en-US.json";
    public static readonly IReadOnlyList<LanguageOption> SupportedLanguages = new[]
    {
            new LanguageOption { Code = "en-US", NativeLabel = "Language", ShortName = "English", FullName = "English" },
            new LanguageOption { Code = "zh-CN", NativeLabel = "语言", ShortName = "中文", FullName = "中文 (简体)" },
            new LanguageOption { Code = "ms-MY", NativeLabel = "Bahasa", ShortName = "Bahasa", FullName = "Bahasa Melayu" },
        };

    private Dictionary<string, string> _translations = new();
    private int _loadVersion;
    public event Action? OnLanguageChanged;

    public string CurrentLanguageName { get; private set; } = "en-US";

    /// <summary>
    /// Populates the default en-US translations synchronously from the embedded resource so startup avoids blocking on HTTP.
    /// </summary>
    public void LoadDefaultLanguage()
    {
      if (_translations.Count > 0 && string.Equals(CurrentLanguageName, "en-US", StringComparison.Ordinal))
      {
        return;
      }

      using var stream = typeof(LanguageService).Assembly.GetManifestResourceStream(DefaultLanguageResourceName);
      if (stream == null)
      {
        return;
      }

      var parsed = JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.DictionaryStringString);
      if (parsed == null)
      {
        return;
      }

      _loadVersion++;
      _translations = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
      CurrentLanguageName = "en-US";
      OnLanguageChanged?.Invoke();
    }

    public async Task LoadLanguageAsync(string langCode)
    {
      var version = ++_loadVersion;
      if (_translations.Count > 0 && string.Equals(CurrentLanguageName, langCode, StringComparison.Ordinal))
      {
        return;
      }

      try
      {
        var response = await http.GetJsonAsync($"i18n/{langCode}.json", AppJsonSerializerContext.Default.DictionaryStringString);
        if (response != null && version == _loadVersion)
        {
          _translations = new Dictionary<string, string>(response, StringComparer.OrdinalIgnoreCase);
          CurrentLanguageName = langCode;
          OnLanguageChanged?.Invoke();
        }
      }
      catch (Exception ex)
      {
        Console.WriteLine($"Failed to load language {langCode}: {ex.Message}");
        if (version == _loadVersion && string.Equals(langCode, "en-US", StringComparison.Ordinal))
        {
          LoadDefaultLanguage();
        }
      }
    }

    public string this[string key]
    {
      get
      {
        if (_translations.TryGetValue(key, out var value))
          return value;
        return key; // Fallback to key if translation not found
      }
    }
  }
}
