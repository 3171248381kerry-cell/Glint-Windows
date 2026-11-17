using System.Windows.Input;

namespace Glint.Windows.Services;

/// <summary>快捷键只保存修饰键和主键，不记录用户按下时的其它内容。</summary>
internal static class HotkeySetting
{
    public const string DefaultModifiers = "Ctrl+Alt";
    public const string DefaultSpeech = "Ctrl+Alt+S";

    public static string NormalizeModifiers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return TryModifiers(value, out var modifiers) && modifiers != ModifierKeys.None ? FormatModifiers(modifiers) : DefaultModifiers;
    }

    public static string NormalizeShortcut(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return TryShortcut(value, out var modifiers, out var key) ? FormatShortcut(modifiers, key) : DefaultSpeech;
    }

    public static bool TryModifiers(string value, out ModifierKeys modifiers)
    {
        modifiers = ModifierKeys.None;
        foreach (var token in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryModifier(token, out var flag)) return false;
            modifiers |= flag;
        }
        return modifiers != ModifierKeys.None;
    }

    public static bool TryShortcut(string value, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        var tokens = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2) return false;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (!TryModifier(tokens[i], out var flag)) return false;
            modifiers |= flag;
        }
        return modifiers != ModifierKeys.None && TryKey(tokens[^1], out key) && key is not (Key.None or Key.F12);
    }

    public static bool TryCapture(KeyEventArgs args, bool modifiersOnly, out string stored)
    {
        stored = "";
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None || key is Key.None or Key.F12) return false;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;
        stored = modifiersOnly ? FormatModifiers(modifiers) : FormatShortcut(modifiers, key);
        return true;
    }

    public static string DisplayModifiers(string value) =>
        string.IsNullOrEmpty(value) ? "未设置" : FormatModifiers(ParseModifiers(value)) + " + 1 … 9";

    public static string DisplayShortcut(string value) =>
        string.IsNullOrEmpty(value) ? "未设置" : value.Replace("+", " + ");

    public static string Summary(string modifiers, string speech)
    {
        string insert = string.IsNullOrEmpty(modifiers) ? "插入快捷键已关闭" : DisplayModifiers(modifiers) + " 插入译文";
        string speak = string.IsNullOrEmpty(speech) ? "朗读快捷键已关闭" : DisplayShortcut(speech) + " 朗读首项";
        return insert + " · " + speak;
    }

    private static ModifierKeys ParseModifiers(string value) => TryModifiers(value, out var modifiers) ? modifiers : ModifierKeys.None;

    private static string FormatModifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    private static string FormatShortcut(ModifierKeys modifiers, Key key) => FormatModifiers(modifiers) + "+" + FormatKey(key);

    private static string FormatKey(Key key) => key is >= Key.D1 and <= Key.D9 ? ((int)key - (int)Key.D1 + 1).ToString() : key.ToString();

    private static bool TryModifier(string token, out ModifierKeys flag)
    {
        flag = token.ToLowerInvariant() switch
        {
            "ctrl" or "control" => ModifierKeys.Control,
            "alt" => ModifierKeys.Alt,
            "shift" => ModifierKeys.Shift,
            "win" or "windows" => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };
        return flag != ModifierKeys.None;
    }

    private static bool TryKey(string token, out Key key)
    {
        if (token.Length == 1 && token[0] is >= '1' and <= '9')
        {
            key = Key.D1 + (token[0] - '1');
            return true;
        }
        return Enum.TryParse(token, true, out key) && key != Key.None;
    }
}
