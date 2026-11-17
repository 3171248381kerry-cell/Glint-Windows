using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glint.Windows.Services;

/// <summary>Preferences only. Candidate text and translation results are never persisted.</summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint");
    private string? _path;
    private bool _launchAtLogin, _autoSpeak, _showPartsOfSpeech = true, _reduceMotion, _hideTaskbarIcon, _mouseInsert = true;
    private string _theme = "System", _translationMode = "Smart", _backgroundStyle = "Frosted", _hudPosition = "Automatic", _voice = "";
    private int _maxTranslations = 3, _dwellMs = 100, _speechRate;
    private double _hudFontSize = 14, _candidateGap = 6, _hudOpacity = .8;
    private string _solidColor = "#1C1C1F", _textColor = "Auto";
    private string _insertModifiers = HotkeySetting.DefaultModifiers, _speechShortcut = HotkeySetting.DefaultSpeech, _targetLanguage = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    [JsonIgnore] public string? PersistenceError { get; private set; }
    public bool TextColorAutoMigrationComplete { get; set; }

    public bool LaunchAtLogin { get => _launchAtLogin; set => Set(ref _launchAtLogin, value); }
    public bool HideTaskbarIcon { get => _hideTaskbarIcon; set => Set(ref _hideTaskbarIcon, value); }
    public bool AutoSpeak { get => _autoSpeak; set => Set(ref _autoSpeak, value); }
    public bool MouseInsert { get => _mouseInsert; set => Set(ref _mouseInsert, value); }
    public bool ShowPartsOfSpeech { get => _showPartsOfSpeech; set => Set(ref _showPartsOfSpeech, value); }
    public bool ReduceMotion { get => _reduceMotion; set => Set(ref _reduceMotion, value); }
    public string Theme { get => _theme; set => Set(ref _theme, Choice(value, "System", "Light", "Dark")); }
    public string TranslationMode { get => _translationMode; set => Set(ref _translationMode, Choice(value, "Smart", "DictionaryFirst", "ModelFirst")); }
    public string BackgroundStyle { get => _backgroundStyle; set => Set(ref _backgroundStyle, Choice(value, "Frosted", "System", "Solid")); }
    public string HudPosition { get => _hudPosition; set => Set(ref _hudPosition, Choice(value, "Automatic", "Above")); }
    public string Voice { get => _voice; set => Set(ref _voice, value ?? ""); }
    public int MaxTranslations { get => _maxTranslations; set => Set(ref _maxTranslations, Math.Clamp(value, 1, 9)); }
    public int DwellMs { get => _dwellMs; set => Set(ref _dwellMs, SnapDwell(value)); }
    public int SpeechRate { get => _speechRate; set => Set(ref _speechRate, Math.Clamp(value, -5, 5)); }
    public double HudFontSize { get => _hudFontSize; set => Set(ref _hudFontSize, Clamp(value, 12, 18, 14)); }
    public double CandidateGap { get => _candidateGap; set => Set(ref _candidateGap, Clamp(value, 0, 32, 6)); }
    public double HudOpacity { get => _hudOpacity; set => Set(ref _hudOpacity, Clamp(value, .4, 1, .8)); }
    public string SolidColor { get => _solidColor; set => Set(ref _solidColor, Color(value, "#1C1C1F")); }
    public string TextColor { get => _textColor; set => Set(ref _textColor, value == "Auto" ? value : Color(value, "Auto")); }
    public string InsertModifiers
    {
        get => _insertModifiers;
        set { Set(ref _insertModifiers, HotkeySetting.NormalizeModifiers(value)); Notify(nameof(ShortcutSummary)); }
    }
    public string SpeechShortcut
    {
        get => _speechShortcut;
        set { Set(ref _speechShortcut, HotkeySetting.NormalizeShortcut(value)); Notify(nameof(ShortcutSummary)); }
    }
    public string TargetLanguage
    {
        get => _targetLanguage;
        set { Set(ref _targetLanguage, Choice(value, "en", "ja", "ko")); Notify(nameof(TargetLanguageName)); }
    }
    [JsonIgnore] public string TargetLanguageName => _targetLanguage switch { "ja" => "日语", "ko" => "韩语", _ => "英语" };
    [JsonIgnore] public string ShortcutSummary => HotkeySetting.Summary(InsertModifiers, SpeechShortcut);

    public static AppSettings Load(string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "settings.json");
        AppSettings? settings = null;
        try { if (File.Exists(path)) settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        settings ??= new AppSettings();
        settings._path = path;
        if (!settings.TextColorAutoMigrationComplete)
        {
            // Older releases stored this light color as the default, before Auto existed.
            if (settings._textColor == "#F2F2F7") settings._textColor = "Auto";
            settings.TextColorAutoMigrationComplete = true;
            settings.Save();
        }
        return settings;
    }

    public void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
            PersistenceError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PersistenceError = "设置暂时无法保存，请检查用户目录权限。"; }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    internal static int SnapDwell(int value) => value <= 62 ? 50 : value <= 87 ? 75 : 100;
    private static string Choice(string value, params string[] choices) => choices.Contains(value) ? value : choices[0];
    private static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    private static string Color(string value, string fallback) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToArray().All(Uri.IsHexDigit) ? value.ToUpperInvariant() : fallback;
}
