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
        _row.Children.Clear();
        Brush text = _settings.TextColor == "Auto"
            ? (Brush)Application.Current.FindResource("TextBrush")
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.TextColor));
        var visible = translations.Take(_settings.MaxTranslations).ToArray();
        bool selectable = allowSelection && _settings.MouseInsert;
        for (int i = 0; i < visible.Length; ++i)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 0 : 16, 0, 0, 0) };
            int index = i;
            entry.Background = Brushes.Transparent;
            if (selectable)
            {
                entry.Cursor = Cursors.Hand;
                entry.MouseLeftButtonUp += (_, e) => { e.Handled = true; TranslationSelected?.Invoke(index); };
            }
            entry.Children.Add(new TextBlock { Text = (i + 1).ToString(), FontSize = 11, Foreground = text, Opacity = .5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            entry.Children.Add(new TextBlock { Text = visible[i].Text, FontSize = _settings.HudFontSize, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Max(80, 650.0 / visible.Length), VerticalAlignment = VerticalAlignment.Center });
            if (_settings.ShowPartsOfSpeech && !string.IsNullOrWhiteSpace(visible[i].PartOfSpeech))
                entry.Children.Add(new TextBlock { Text = visible[i].PartOfSpeech, FontSize = 11, Foreground = text, Opacity = .65, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _row.Children.Add(entry);
        }
        // Keep a readable surface if the system declines the material request.
        _surface.Background = (Brush)Application.Current.FindResource("HudGlassBrush");
        _surface.BorderBrush = (Brush)Application.Current.FindResource("HudGlassBorderBrush");
        bool wasVisible = IsVisible;
        if (!wasVisible) { Opacity = 0; Show(); }
        SetSelectable(selectable);
        UpdateLayout();
        Position(candidateBounds, inputLine, source);
        if (Animate && !wasVisible) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(80)));
        else Opacity = 1;
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
    public void Dismiss()
    {
        SetSelectable(false);
        int generation = ++_generation;
        if (!IsVisible) return;
        BeginAnimation(OpacityProperty, null);
        if (!Animate) { _backdrop.Hide(); Hide(); return; }
        var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(60));
        animation.Completed += (_, _) => { if (_generation == generation) { _backdrop.Hide(); Hide(); } };
        BeginAnimation(OpacityProperty, animation);
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
            Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 9, 9);
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
            _surface.Background = new SolidColorBrush(Color.FromArgb(190, tint.R, tint.G, tint.B));
            _backdrop.SetOpacity(Opacity);
        }
        else _backdrop.Hide();
    }

    private void OpacityChanged(object? sender, EventArgs e) => _backdrop.SetOpacity(Opacity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
