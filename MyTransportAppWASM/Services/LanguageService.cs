using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace MyTransportAppWASM.Services
{
    public class LanguageOption
    {
        public string Code { get; set; } = string.Empty;
        public string NativeLabel { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
    }

    public class LanguageService
    {
        public static readonly IReadOnlyList<LanguageOption> SupportedLanguages = new[]
        {
            new LanguageOption { Code = "en-US", NativeLabel = "Language", ShortName = "English", FullName = "English" },
            new LanguageOption { Code = "zh-CN", NativeLabel = "语言", ShortName = "中文", FullName = "中文 (简体)" },
            new LanguageOption { Code = "ms-MY", NativeLabel = "Bahasa", ShortName = "Bahasa", FullName = "Bahasa Melayu" },
        };

        private readonly HttpClient _http;
        private Dictionary<string, string> _translations = new();
        public event Action? OnLanguageChanged;

        public string CurrentLanguageName { get; private set; } = "en-US";

        public LanguageService(HttpClient http)
        {
            _http = http;
        }

        public async Task LoadLanguageAsync(string langCode)
        {
            try
            {
                var response = await _http.GetFromJsonAsync<Dictionary<string, string>>($"i18n/{langCode}.json");
                if (response != null)
                {
                    _translations = response;
                    CurrentLanguageName = langCode;
                    OnLanguageChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load language {langCode}: {ex.Message}");
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
