using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;

namespace AudioSourceMixer.Desktop.Localization;

public sealed class LocalizedResourceManager
{
    private const string Prefix = "AudioSourceMixer.Desktop.Localization.strings.";
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _resources;

    public LocalizedResourceManager()
    {
        _resources = LocalizationService.SupportedLanguages.ToDictionary(code => code, Load, StringComparer.OrdinalIgnoreCase);
        var reference = _resources[LocalizationService.EnglishLanguage].Keys.Order(StringComparer.Ordinal).ToArray();
        foreach (var (culture, values) in _resources)
        {
            var keys = values.Keys.Order(StringComparer.Ordinal).ToArray();
            if (!reference.SequenceEqual(keys, StringComparer.Ordinal))
                throw new InvalidDataException($"Localization key set mismatch for {culture}.");
            if (values.Any(item => string.IsNullOrWhiteSpace(item.Value)))
                throw new InvalidDataException($"Localization contains an empty value for {culture}.");
        }
    }

    public IReadOnlyCollection<string> Keys => _resources[LocalizationService.EnglishLanguage].Keys.ToArray();

    public string GetString(string key, string culture)
    {
        var normalized = LocalizationService.NormalizeLanguage(culture);
        if (_resources[normalized].TryGetValue(key, out var value)) return value;
        if (_resources[LocalizationService.EnglishLanguage].TryGetValue(key, out value)) return value;
        return _resources[normalized]["Common.MissingText"];
    }

    private static IReadOnlyDictionary<string, string> Load(string culture)
    {
        var resourceName = $"{Prefix}{culture}.json";
        using var stream = typeof(LocalizedResourceManager).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Embedded localization resource is missing: {resourceName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"Embedded localization resource is invalid: {resourceName}");
    }
}

public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string ChineseLanguage = "zh-CN";
    public const string EnglishLanguage = "en-US";
    public static IReadOnlyList<string> SupportedLanguages { get; } = [ChineseLanguage, EnglishLanguage];
    public static LocalizationService Current { get; } = new();

    private readonly LocalizedResourceManager _resources = new();
    private string _currentLanguage = ChineseLanguage;

    private LocalizationService() { }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CultureChanged;
    public string CurrentLanguage => _currentLanguage;
    public IReadOnlyCollection<string> ResourceKeys => _resources.Keys;
    public string this[string key] => _resources.GetString(key, _currentLanguage);

    public static string NormalizeLanguage(string? language) =>
        string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase) ? EnglishLanguage : ChineseLanguage;

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, this[key], arguments);

    public void SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
        {
            ApplyCultureAndTypography(normalized);
            return;
        }
        _currentLanguage = normalized;
        ApplyCultureAndTypography(normalized);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(System.Windows.Data.Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyCultureAndTypography(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        if (System.Windows.Application.Current is null) return;
        System.Windows.Application.Current.Resources["ApplicationFont"] = language == ChineseLanguage
            ? new System.Windows.Media.FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Global User Interface")
            : new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Arial, Global User Interface");
        var xmlLanguage = XmlLanguage.GetLanguage(language);
        foreach (System.Windows.Window window in System.Windows.Application.Current.Windows) window.Language = xmlLanguage;
    }
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key)) return string.Empty;
        return new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationService.Current,
            Mode = System.Windows.Data.BindingMode.OneWay
        }.ProvideValue(serviceProvider);
    }
}

public sealed record LanguageOption(string Code, string DisplayName);
