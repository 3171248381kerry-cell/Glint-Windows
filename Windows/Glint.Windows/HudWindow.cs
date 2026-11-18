using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using Glint.Windows.Services;
using Glint.Windows.Platform;

namespace Glint.Windows;

/// <summary>A single non-activating panel; it never takes focus from the IME.</summary>
public sealed class HudWindow : Window
{
    private readonly Border _surface;
    private readonly StackPanel _row;
    private readonly List<TranslationEntry> _entries = [];
    private readonly RectangleGeometry _clip = new() { RadiusX = 9, RadiusY = 9 };
    private readonly SolidColorBrush _backdropTint = new();
    private readonly AppSettings _settings;
    private readonly CompositionBackdrop _backdrop = new();
    private readonly DependencyPropertyDescriptor _opacityDescriptor;
    private int _generation;
    private nint _handle;
    public event Action<int>? TranslationSelected;
    internal bool HasCompositionBackdrop { get; private set; }
    internal int CompositionError => _backdrop.Error;
    public HudWindow(AppSettings settings)
    {
        _settings = settings;
        _opacityDescriptor = DependencyPropertyDescriptor.FromProperty(OpacityProperty, typeof(Window));
        _opacityDescriptor.AddValueChanged(this, OpacityChanged);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Width;
        Height = 36;
        IsHitTestVisible = false;
        _row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _surface = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(12, 0, 12, 0), Child = _row };
        Content = _surface;
        Clip = _clip;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr(_handle, -20).ToInt64();
            SetWindowLongPtr(_handle, -20, new nint(style | 0x08000000 | 0x80 | 0x20)); // NOACTIVATE | TOOLWINDOW | TRANSPARENT
            HwndSource.FromHwnd(_handle).AddHook(WindowMessage);
        };
    }

    public void Present(IReadOnlyList<TranslationResult> translations, Rect candidateBounds, Rect? inputLine = null, string source = "", bool allowSelection = false)
    {
        if (translations.Count == 0) { Dismiss(); return; }
        ++_generation;
        BeginAnimation(OpacityProperty, null);
        Brush text = _settings.TextColor == "Auto"
            ? (Brush)Application.Current.FindResource("TextBrush")
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.TextColor));
        int count = Math.Min(translations.Count, _settings.MaxTranslations);
        bool selectable = allowSelection && _settings.MouseInsert;
        for (int i = 0; i < count; ++i)
        {
            if (i == _entries.Count)
            {
                int index = i;
                var entry = new TranslationEntry(index);
                entry.Row.MouseLeftButtonUp += (_, e) =>
                {
                    if (!IsHitTestVisible || !entry.Row.IsVisible) return;
                    e.Handled = true;
                    TranslationSelected?.Invoke(index);
                };
                _entries.Add(entry);
                _row.Children.Add(entry.Row);
            }
            _entries[i].Update(translations[i], text, _settings.HudFontSize, Math.Max(80, 650.0 / count), _settings.ShowPartsOfSpeech);
            _entries[i].Row.Cursor = selectable ? Cursors.Hand : null;
        }
        for (int i = count; i < _entries.Count; ++i) _entries[i].Clear();
        _surface.BorderBrush = (Brush)Application.Current.FindResource("HudGlassBorderBrush");
        bool wasVisible = IsVisible;
        if (!wasVisible) { Opacity = 0; Show(); }
        SetSelectable(selectable);
        UpdateLayout();
        Position(candidateBounds, inputLine, source);
        if (Animate && !wasVisible) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(80)));
        else Opacity = 1;
    }

    internal void Reposition(Rect candidateBounds, Rect? inputLine, string source)
    {
        if (IsVisible) Position(candidateBounds, inputLine, source);
    }

    protected override void OnClosed(EventArgs e)
    {
        ++_generation;
        BeginAnimation(OpacityProperty, null);
        _opacityDescriptor.RemoveValueChanged(this, OpacityChanged);
        _backdrop.Dispose();
        base.OnClosed(e);
    }

    private bool Animate => !_settings.ReduceMotion && SystemParameters.ClientAreaAnimation;
    public void MarkStale()
    {
        SetSelectable(false);
        ++_generation;
        BeginAnimation(OpacityProperty, null);
        if (IsVisible) Opacity = 1;
    }
    public void Dismiss(bool immediate = false)
    {
        SetSelectable(false);
        int generation = ++_generation;
        if (!IsVisible) return;
        BeginAnimation(OpacityProperty, null);
        if (immediate || !Animate) { HideContents(); return; }
        var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(60));
        animation.Completed += (_, _) => { if (_generation == generation) HideContents(); };
        BeginAnimation(OpacityProperty, animation);
    }

    private void HideContents()
    {
        _backdrop.Hide();
        Hide();
        foreach (var entry in _entries) entry.Clear();
    }

    private void SetSelectable(bool selectable)
    {
        IsHitTestVisible = selectable;
        if (_handle == 0) return;
        long style = GetWindowLongPtr(_handle, -20).ToInt64();
        SetWindowLongPtr(_handle, -20, new nint(selectable ? style & ~0x20L : style | 0x20L));
    }

    private static nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new nint(3); } // WM_MOUSEACTIVATE: MA_NOACTIVATE
        return 0;
    }

    private void Position(Rect candidate, Rect? inputLine, string source)
    {
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle((int)candidate.Left, (int)candidate.Top, Math.Max(1, (int)candidate.Width), Math.Max(1, (int)candidate.Height)));
        var work = screen.WorkingArea;
        // First move to the target monitor so WPF processes WM_DPICHANGED before measuring.
        SetWindowPos(_handle, new nint(-1), (int)candidate.Left, (int)candidate.Bottom, 0, 0, 0x0011);
        UpdateLayout();
        double scale = GetDpiForWindow(_handle) / 96.0;
        if (scale <= 0) scale = 1;
        MaxWidth = Math.Max(200, (work.Width - 16) / scale);
        UpdateLayout();
        if (ActualWidth > 0 && ActualHeight > 0)
            _clip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        int width = (int)Math.Ceiling(ActualWidth * scale);
        int height = (int)Math.Ceiling(Height * scale);
        int gap = (int)Math.Round(_settings.CandidateGap * scale);
        bool weChat = source.Contains("微信", StringComparison.Ordinal);
        if (weChat && inputLine is null && NativeMethods.TryGetWeChatTextLine(candidate, out var weChatLine)) inputLine = weChatLine;
        else if (inputLine is null && NativeMethods.TryGetInputLine(candidate, out var detected)) inputLine = detected;
        var placed = HudPlacement.Place(
            candidate, width, height, gap,
            new Rect(work.Left, work.Top, work.Width, work.Height),
            inputLine, _settings.HudPosition == "Above", weChat);
        SetWindowPos(_handle, new nint(-1), placed.X, placed.Y, width, height, 0x0010);
        HasCompositionBackdrop = _settings.BackgroundStyle != "Solid" && _backdrop.Show(_handle, placed.X, placed.Y, width, height, scale);
        if (HasCompositionBackdrop)
        {
            var tint = ((SolidColorBrush)Application.Current.FindResource("HudGlassBrush")).Color;
            _backdropTint.Color = Color.FromArgb(190, tint.R, tint.G, tint.B);
            _surface.Background = _backdropTint;
            _backdrop.SetOpacity(Opacity);
        }
        else
        {
            _backdrop.Hide();
            _surface.Background = (Brush)Application.Current.FindResource("HudGlassBrush");
        }
    }

    private void OpacityChanged(object? sender, EventArgs e) => _backdrop.SetOpacity(Opacity);

    private sealed class TranslationEntry
    {
        internal StackPanel Row { get; }
        private readonly TextBlock _number;
        private readonly TextBlock _text = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _part = new() { FontSize = 11, Opacity = .65, Margin = new Thickness(5, 1, 0, 0) };

        internal TranslationEntry(int index)
        {
            Row = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(index == 0 ? 0 : 16, 0, 0, 0), Background = Brushes.Transparent
            };
            _number = new TextBlock
            {
                Text = (index + 1).ToString(), FontSize = 11, Opacity = .5, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            Row.Children.Add(_number);
            Row.Children.Add(_text);
            Row.Children.Add(_part);
        }

        internal void Update(TranslationResult translation, Brush color, double fontSize, double maxWidth, bool showPart)
        {
            _number.Foreground = _text.Foreground = _part.Foreground = color;
            _text.Text = translation.Text;
            _text.FontSize = fontSize;
            _text.MaxWidth = maxWidth;
            _part.Text = showPart ? translation.PartOfSpeech ?? "" : "";
            _part.Visibility = string.IsNullOrWhiteSpace(_part.Text) ? Visibility.Collapsed : Visibility.Visible;
            Row.Visibility = Visibility.Visible;
        }

        internal void Clear()
        {
            Row.Visibility = Visibility.Collapsed;
            _text.Text = "";
            _part.Text = "";
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
