using System.Globalization;
using System.Text.Json;

sealed record LocaleInfo(string Code, string Name, string Direction);

sealed class LocaleStore
{
    private readonly Dictionary<string, LocaleInfo> _locales;
    private readonly object _gate = new();
    private string _language;

    public LocaleStore(string webRoot)
    {
        _locales = Directory.EnumerateFiles(Path.Combine(webRoot, "locales"), "*.json")
            .Select(ReadInfo)
            .Where(info => info is not null)
            .ToDictionary(info => info!.Code, info => info!, StringComparer.OrdinalIgnoreCase);
        if (_locales.Count == 0) throw new InvalidOperationException("No locale catalogs were found.");

        var saved = ReadSavedLanguage();
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        _language = IsSupported(saved) ? saved! : IsSupported(system) ? system :
            IsSupported("ko") ? "ko" : _locales.Keys.First();
    }

    public string Language { get { lock (_gate) return _language; } }
    public LocaleInfo[] Locales => _locales.Values.OrderBy(locale => locale.Name).ToArray();
    public bool IsSupported(string? code) => !string.IsNullOrWhiteSpace(code) && _locales.ContainsKey(code);

    public bool SetLanguage(string? code)
    {
        if (!IsSupported(code)) return false;
        lock (_gate) _language = code!.ToLowerInvariant();
        SaveLanguage(_language);
        return true;
    }

    private static LocaleInfo? ReadInfo(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var code = root.GetProperty("$code").GetString()?.Trim().ToLowerInvariant();
            var name = root.GetProperty("$name").GetString()?.Trim();
            var direction = root.GetProperty("$direction").GetString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name) ||
                direction is not ("ltr" or "rtl")) return null;
            return new LocaleInfo(code, name, direction);
        }
        catch { return null; }
    }

    private static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassroomLive", "settings.json");

    private static string? ReadSavedLanguage()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return document.RootElement.GetProperty("language").GetString()?.Trim().ToLowerInvariant();
        }
        catch { return null; }
    }

    private static void SaveLanguage(string code)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { language = code }));
            File.Move(temporary, SettingsPath, true);
        }
        catch { /* 언어는 다음 실행에 기본값으로 돌아가도 기능 자체는 계속 동작한다. */ }
    }
}

sealed record LanguageRequest(string Code);
