using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Glint.Windows.Platform;
using Microsoft.Win32;

namespace Glint.Windows.Services;

public sealed class AppController : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly TranslationService _translations = new();
    private readonly SpeechService _speech = new();
    private readonly CandidateMonitor _monitor;
    private readonly TextInsertionService _insertion = new();
    private readonly HudWindow _hud;
    private readonly bool _testMode;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private CancellationTokenSource? _preview;
    private GlobalHotKeys? _hotkeys;
    private CandidateSnapshot? _snapshot;
    private IReadOnlyList<TranslationResult> _results = [];
    private bool _isPaused, _disposed, _isInstallingModel;
    private string _monitorStatus = "正在准备翻译", _modelInstallStatus = "", _actionStatus = "";
    public AppSettings Settings { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value) return;
            _isPaused = value;
            if (value) { _monitor.Stop(); Invalidate(true); _speech.Stop(); _monitorStatus = "已暂停"; }
            else if (_translations.IsAvailable) { _monitorStatus = "等待中文输入法候选"; if (!_testMode) _monitor.Start(); }
            Notify(nameof(IsPaused)); Notify(nameof(IsMonitoring)); Notify(nameof(MonitorStatus));
        }
    }
    public bool IsMonitoring => !IsPaused && _translations.IsAvailable && !_disposed;
    public string MonitorStatus => _monitorStatus;
    public string TranslationStatus => Settings.TargetLanguage == "en"
        ? _translations.Status + (ModelReady ? " · OPUS-MT 已就绪" : " · 短句模型未安装")
        : _translations.Model.IsReady(Settings.TargetLanguage) ? Settings.TargetLanguageName + "本地模型已就绪" : Settings.TargetLanguageName + "模型尚未安装";
    public string ModelAvailability => _translations.Model.Availability;
    public bool ModelReady => _translations.Model.IsAvailable;
    public IReadOnlyList<string> VoiceNames => _speech.VoiceNames;
    public IReadOnlyList<string> SpeechVoiceNames() => _speech.NamesFor(Settings.TargetLanguage);
    public string ModelInstallStatus => _modelInstallStatus;
    public bool IsInstallingModel => _isInstallingModel;
    public string ActionStatus => Settings.PersistenceError ?? _actionStatus;
    internal TranslationService Translations => _translations;
    internal HudWindow Hud => _hud;

    public AppController(bool testMode = false, AppSettings? settings = null)
    {
        _testMode = testMode;
        Settings = settings ?? AppSettings.Load();
        if (!testMode) _translations.Model.SelectTarget(Settings.TargetLanguage);
        _hud = new HudWindow(Settings);
        _hud.TranslationSelected += Insert;
        _monitor = new CandidateMonitor(() => Settings.DwellMs);
        _monitor.CandidateInvalidated += () => Dispatch(() => Invalidate(false));
        _monitor.CandidateChanged += snapshot => Dispatch(() => CandidateChanged(snapshot));
        _monitor.StatusChanged += status => Dispatch(() => { if (!IsPaused) { _monitorStatus = status; Notify(nameof(MonitorStatus)); } });
        Settings.PropertyChanged += SettingsChanged;
        ApplyTheme();
        if (!testMode) SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        _monitorStatus = _translations.IsAvailable ? "等待中文输入法候选" : "内置词典尚未就绪";
    }

    public void Start(MainWindow window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _hotkeys = new GlobalHotKeys(HwndSource.FromHwnd(handle));
        if (!_testMode && _translations.IsAvailable && !IsPaused) _monitor.Start();
    }

    private void Invalidate(bool dismiss, bool immediate = false)
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
        _snapshot = null;
        _results = [];
        _hotkeys?.Clear();
        _speech.Stop();
        if (dismiss) _hud.Dismiss(immediate); else _hud.MarkStale();
    }

    private async void CandidateChanged(CandidateSnapshot? snapshot)
    {
        if (_disposed || IsPaused) return;
        if (snapshot is null) { Invalidate(true, immediate: true); return; }
        if (_monitor.CurrentSnapshot is not { } current || current.Revision != snapshot.Revision) return;
        _preview?.Cancel();
        if (_snapshot?.Revision == snapshot.Revision)
        {
            _snapshot = snapshot;
            if (_results.Count > 0)
                _hud.Reposition(snapshot.Bounds, snapshot.InputLine, snapshot.Source);
            return;
        }
        Invalidate(false);
        _snapshot = snapshot;
        var request = new CancellationTokenSource();
        _request = request;
        var token = request.Token;
        try
        {
            var results = await _translations.TranslateAsync(snapshot.Text, Settings.TargetLanguage, Mode, token);
            if (_disposed || token.IsCancellationRequested || _monitor.CurrentSnapshot?.Revision != snapshot.Revision || _snapshot?.Revision != snapshot.Revision) return;
            _results = results;
            Notify(nameof(ModelReady)); Notify(nameof(TranslationStatus));
            if (results.Count == 0) { _hud.Dismiss(); return; }
            _hud.Present(results, _snapshot.Bounds, _snapshot.InputLine, _snapshot.Source, allowSelection: true);
            RegisterCandidateHotkeys();
            if (Settings.AutoSpeak) _speech.Speak(results[0].SpeakText ?? results[0].Text, Settings.Voice, Settings.SpeechRate, Settings.TargetLanguage);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            if (_snapshot?.Revision == snapshot.Revision) { _results = []; _hud.Dismiss(); _hotkeys?.Clear(); }
        }
    }

    private TranslationMode Mode => Enum.TryParse<TranslationMode>(Settings.TranslationMode, out var mode) ? mode : TranslationMode.Smart;
    private void RegisterCandidateHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.Clear();
        bool success = true;
        if (HotkeySetting.TryModifiers(Settings.InsertModifiers, out var insertModifiers))
        {
            for (int i = 0; i < Math.Min(_results.Count, Settings.MaxTranslations); i++)
            {
                int index = i;
                success &= _hotkeys.Register(100 + i, insertModifiers, (Key)((int)Key.D1 + i), () => Insert(index));
            }
        }
        if (HotkeySetting.TryShortcut(Settings.SpeechShortcut, out var speechModifiers, out var speechKey) &&
            !(speechKey is >= Key.D1 and <= Key.D9 && speechModifiers == insertModifiers))
        {
            success &= _hotkeys.Register(110, speechModifiers, speechKey, () =>
            {
                if (_snapshot is { } s && _monitor.CurrentSnapshot?.Revision == s.Revision && _results.Count > 0)
                    _speech.Speak(_results[0].SpeakText ?? _results[0].Text, Settings.Voice, Settings.SpeechRate, Settings.TargetLanguage);
            });
        }
        _actionStatus = success ? "" : "部分译文快捷键被其他应用占用。";
        Notify(nameof(ActionStatus));
    }
    private async void Insert(int index)
    {
        if (_snapshot is not { } snapshot || index < 0 || index >= _results.Count || _monitor.CurrentSnapshot?.Revision != snapshot.Revision) return;
        string text = _results[index].Text;
        await _insertion.TryInsertAsync(snapshot, text);
    }

    public async Task<string> TestTranslationAsync(string text)
    {
        var results = await _translations.TranslateAsync(text, Settings.TargetLanguage, Mode, _lifetime.Token);
        Notify(nameof(ModelReady)); Notify(nameof(TranslationStatus));
        if (results.Count == 0) return Settings.TargetLanguage == "en"
            ? (ModelReady ? "没有可用译文，请尝试其他中文词句。" : "词典未收录这个词句。安装 OPUS-MT 后可翻译短句。")
            : (_translations.Model.IsReady(Settings.TargetLanguage) ? "没有可用译文，请尝试其他中文词句。" : "这个语言的本地模型尚未安装。");
        return string.Join("\n", results.Take(Settings.MaxTranslations).Select((r, i) => $"{i + 1}  {r.Text}{(Settings.ShowPartsOfSpeech && !string.IsNullOrEmpty(r.PartOfSpeech) ? "  " + r.PartOfSpeech : "")}    · {r.Source}"));
    }

    public async void PreviewHud()
    {
        _preview?.Cancel(); _preview?.Dispose();
        var preview = new CancellationTokenSource();
        _preview = preview;
        try
        {
            var results = await _translations.TranslateAsync("你好", Settings.TargetLanguage, ct: preview.Token);
            if (preview.IsCancellationRequested || _disposed || _monitor.CurrentSnapshot is not null) return;
            var main = Application.Current.MainWindow;
            var origin = main is { IsVisible: true } ? main.PointToScreen(new Point(Math.Max(240, main.ActualWidth * .38), Math.Min(main.ActualHeight - 90, 460))) : new Point(420, 400);
            _hud.Present(results, new Rect(origin.X, origin.Y, 240, 32));
            await Task.Delay(3500, preview.Token);
            if (_monitor.CurrentSnapshot is null) _hud.Dismiss();
        }
        catch (OperationCanceledException) { }
    }
    public void SpeakPreview() => _speech.Speak(Settings.TargetLanguage switch { "ja" => "こんにちは", "ko" => "안녕하세요", _ => "Hello. Nice to meet you." }, Settings.Voice, Settings.SpeechRate, Settings.TargetLanguage);
    public void OpenDataFolder()
    {
        Directory.CreateDirectory(AppSettings.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.DataDirectory) { UseShellExecute = true });
    }
    public async Task InstallModelAsync()
    {
        if (_isInstallingModel || _testMode) return;
        _isInstallingModel = true; Notify(nameof(IsInstallingModel));
        _modelInstallStatus = "正在准备本地翻译模型…"; Notify(nameof(ModelInstallStatus));
        try
        {
            await _translations.Model.InstallAsync(new Progress<string>(status => { if (!_disposed) { _modelInstallStatus = status; Notify(nameof(ModelInstallStatus)); } }), _lifetime.Token);
            _modelInstallStatus = ModelReady ? "OPUS-MT 已就绪，短句翻译在本机完成。" : "安装尚未完成，请重试。";
        }
        catch (NotSupportedException) { _modelInstallStatus = "本地模型目前支持 Windows x64。内置词典仍可使用。"; }
        catch (OperationCanceledException) { _modelInstallStatus = "模型准备已取消。"; }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception)
        { _modelInstallStatus = "安装未完成。请检查网络后重试，已有词典仍可使用。"; }
        finally
        {
            _translations.ClearCache();
            _isInstallingModel = false;
            Notify(nameof(IsInstallingModel)); Notify(nameof(ModelInstallStatus)); Notify(nameof(ModelReady)); Notify(nameof(ModelAvailability)); Notify(nameof(TranslationStatus));
            Invalidate(true);
        }
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (args.PropertyName == nameof(AppSettings.Theme)) ApplyTheme();
        else if (args.PropertyName == nameof(AppSettings.LaunchAtLogin) && !_testMode) UpdateStartup();
        else if (args.PropertyName is nameof(AppSettings.Voice) or nameof(AppSettings.SpeechRate)) SpeakPreview();
        else if (args.PropertyName == nameof(AppSettings.AutoSpeak) && !Settings.AutoSpeak) _speech.Stop();
        if (args.PropertyName is nameof(AppSettings.TranslationMode) or nameof(AppSettings.DwellMs) or nameof(AppSettings.TargetLanguage))
        {
            Invalidate(true);
            if (args.PropertyName == nameof(AppSettings.TargetLanguage))
            {
                _preview?.Cancel();
                _translations.Model.SelectTarget(Settings.TargetLanguage);
            }
            Notify(nameof(TranslationStatus));
            if (!_testMode && !IsPaused) { _monitor.Stop(); _monitor.Start(); }
        }
        else if (_snapshot is { } snapshot && _results.Count > 0) { _hud.Present(_results, snapshot.Bounds, snapshot.InputLine, snapshot.Source, allowSelection: true); RegisterCandidateHotkeys(); }
        Notify(nameof(ActionStatus));
    }
    private void UpdateStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (Settings.LaunchAtLogin) key.SetValue("Glint", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("Glint", false);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _actionStatus = "登录启动设置未能应用，请检查系统权限。";
            Notify(nameof(ActionStatus));
        }
    }
    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatch(() =>
    {
        if (Settings.Theme != "System") return;
        ApplyTheme();
        if (_snapshot is { } snapshot && _results.Count > 0)
            _hud.Present(_results, snapshot.Bounds, snapshot.InputLine, snapshot.Source, allowSelection: true);
    });
    public void ApplyTheme()
    {
        bool dark = Settings.Theme == "Dark";
        if (Settings.Theme == "System")
        {
            try { dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
            catch (System.Security.SecurityException) { dark = false; }
        }
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) });
    }
    public void Quit() => Application.Current.Shutdown();
    private void Dispatch(Action action) { if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.InvokeAsync(() => { if (!_disposed) action(); }); }
    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        Settings.PropertyChanged -= SettingsChanged;
        _preview?.Cancel(); _preview?.Dispose();
        Invalidate(true);
        _monitor.Dispose(); _hotkeys?.Dispose();
        _hud.Close();
        _speech.Dispose(); _translations.Dispose();
        _lifetime.Dispose();
    }
}
