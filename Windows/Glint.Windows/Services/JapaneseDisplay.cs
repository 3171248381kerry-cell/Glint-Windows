namespace Glint.Windows.Services;

/// <summary>
/// 日语译文的固定显示：有汉字时先保留汉字，再把整段平假名放进括号；
/// 没有汉字时直接显示假名，不额外加括号。
/// </summary>
internal static class JapaneseDisplay
{
    public static string Format(string surface, string? reading)
    {
        surface = surface.Trim();
        if (surface.Length == 0 || !surface.Any(IsKanji)) return surface;
        var hiragana = Hiragana(reading);
        return hiragana is null ? surface : surface + "（" + hiragana + "）";
    }

    public static string Pronunciation(string surface, string? reading)
    {
        var hiragana = Hiragana(reading);
        return surface.Any(IsKanji) && hiragana is not null ? hiragana : surface.Trim();
    }

    public static bool IsUsable(string text)
    {
        if (text.Length is 0 or > 1000) return false;
        int latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return latin <= 2 && text.Any(c => IsKanji(c) || IsKana(c)) && !text.Any(IsHangul);
    }

    public static void Check()
    {
        if (Format("テスト", null) != "テスト") throw new InvalidOperationException("无汉字的片假名应直接显示");
        if (Format("こんにちは", "こんにちは") != "こんにちは") throw new InvalidOperationException("无汉字的平假名应直接显示");
        if (Format("学生", "がくせい") != "学生（がくせい）") throw new InvalidOperationException("有汉字时应在括号中附上平假名");
        if (Format("学生", "ガクセイ") != "学生（がくせい）") throw new InvalidOperationException("读音里的片假名应转成平假名");
        if (Format("食べます", "たべます") != "食べます（たべます）") throw new InvalidOperationException("汉字与假名混写时仍应附上整段平假名");
        if (Format("東京タワー", "とうきょうたわー") != "東京タワー（とうきょうたわー）") throw new InvalidOperationException("片假名应留在汉字一侧");
        if (Format("元気?", "げんき?") != "元気?（げんき?）") throw new InvalidOperationException("读音里的标点应保留");
        if (Format("日本", "日本") != "日本") throw new InvalidOperationException("仍含汉字的读音不能放进括号");
        if (Format("日本", null) != "日本") throw new InvalidOperationException("没有读音时只显示汉字");
        if (Pronunciation("学生", "ガクセイ") != "がくせい") throw new InvalidOperationException("朗读应使用平假名");
        if (Pronunciation("テスト", null) != "テスト") throw new InvalidOperationException("没有汉字时朗读原文假名");
        if (!IsUsable("学生（がくせい）") || !IsUsable("こんにちは") || IsUsable("hello")) throw new InvalidOperationException("日语结果校验不正确");
    }

    private static string? Hiragana(string? reading)
    {
        if (string.IsNullOrWhiteSpace(reading)) return null;
        var chars = new List<char>(reading.Length);
        foreach (var c in reading.Trim())
        {
            if (c is >= '\u30A1' and <= '\u30F6') chars.Add((char)(c - 0x60));
            else if (c is >= '\u3041' and <= '\u3096' or 'ー' or '・' or >= '0' and <= '9' || char.IsPunctuation(c) || char.IsSymbol(c)) chars.Add(c);
            else if (!char.IsWhiteSpace(c)) return null;
        }
        var value = new string(chars.ToArray());
        return value.Any(c => c is >= '\u3041' and <= '\u3096') ? value : null;
    }

    private static bool IsKanji(char c) => DictionarySurface.IsHan(c);
    private static bool IsKana(char c) => c is >= '\u3040' and <= '\u30FF';
    private static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3';
}
