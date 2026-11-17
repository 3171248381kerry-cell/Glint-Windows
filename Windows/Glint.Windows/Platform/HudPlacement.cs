using System.Windows;

namespace Glint.Windows.Platform;

/// <summary>
/// 屏幕坐标 Y 向下。候选栏在输入行上方时，译文放到候选栏上方；
/// 候选栏在输入行下方时，译文放到候选栏下方。避免盖住正在输入的那一行。
/// </summary>
internal static class HudPlacement
{
    internal readonly record struct Result(int X, int Y);

    // Only use a list inside the candidate container. Retain its vertical bounds
    // for input-line avoidance while anchoring to the candidate row itself.
    internal static Rect AlignToList(Rect container, Rect list)
    {
        if (list.IsEmpty || list.Width <= 0 || list.Height <= 0 ||
            list.Left < container.Left || list.Left >= container.Right ||
            list.Right > container.Right + 2 || list.Bottom <= container.Top || list.Top >= container.Bottom)
            return container;
        return new Rect(list.Left, container.Top, container.Right - list.Left, container.Height);
    }

    internal static Result Place(
        Rect candidate,
        int panelWidth,
        int panelHeight,
        int gap,
        Rect work,
        Rect? inputLine,
        bool forceAbove,
        bool weChat = false)
    {
        if (weChat) return PlaceWeChat(candidate, panelWidth, panelHeight, gap, work, inputLine, forceAbove);
        if (inputLine is { } supplied && !KeepMicrosoftLine(candidate, supplied)) inputLine = null;
        int x = Clamp((int)candidate.Left, (int)work.Left + 4, Math.Max((int)work.Left + 4, (int)work.Right - panelWidth - 4));
        int belowY = (int)candidate.Bottom + gap;
        // 候选栏在原文下方、且下面还有屏幕空间时，译文必须跟在候选栏下面。
        bool textBelow = inputLine is { } line && line.Top + line.Height / 2 > candidate.Bottom - 4;
        bool below = !forceAbove && !textBelow && belowY + panelHeight <= work.Bottom - 4;
        int y = below ? belowY : textBelow ? (int)candidate.Top - gap - panelHeight : Above(candidate, inputLine, panelHeight, gap);
        if (!below && y < work.Top + 4) y = belowY;
        y = Clamp(y, (int)work.Top + 4, Math.Max((int)work.Top + 4, (int)work.Bottom - panelHeight - 4));
        return new Result(x, y);
    }

    internal static void Check()
    {
        var work = new Rect(0, 0, 1400, 900);
        var outer = new Rect(40, 100, 500, 90);
        if (AlignToList(outer, new Rect(58, 145, 470, 40)).Left != 58 ||
            AlignToList(outer, new Rect(20, 145, 470, 40)) != outer)
            throw new InvalidOperationException("候选列表的左边缘无效");
        if (DoubaoCandidateBridge.CandidateRowBounds(outer, 96).Left != 64 ||
            DoubaoCandidateBridge.CandidateRowBounds(outer, 144).Left != 76)
            throw new InvalidOperationException("豆包候选栏的左边缘缩放无效");
        var roomBelow = Place(new Rect(80, 180, 420, 44), 240, 36, 6, work, new Rect(90, 250, 2, 22), false);
        if (roomBelow.Y + 36 > 180) throw new InvalidOperationException("候选栏在输入行上方时，译文必须在候选栏上方");
        var belowCaret = Place(new Rect(80, 320, 420, 44), 240, 36, 6, work, new Rect(90, 250, 2, 22), false);
        if (belowCaret.Y < 364) throw new InvalidOperationException("候选栏在输入行下方时，译文没有跟到下方");
        var forced = Place(new Rect(80, 320, 420, 44), 240, 36, 6, work, new Rect(90, 250, 2, 22), true);
        if (forced.Y + 36 > 320 - 24) throw new InvalidOperationException("指定上方时没有为空出原文");
        // 微软拼音贴在屏幕底部时，译文改到候选栏上方，但原文就贴在候选栏顶上，必须先空出这一行。
        var microsoft = Place(new Rect(70, 760, 280, 36), 220, 32, 6, new Rect(0, 0, 1400, 820), new Rect(80, 732, 120, 28), false);
        if (microsoft.Y + 32 > 732) throw new InvalidOperationException("微软拼音的译文盖住了候选栏上方的原文");
        var weChatAboveText = Place(new Rect(80, 220, 360, 40), 180, 32, 6, work, new Rect(90, 180, 90, 24), false, true);
        if (weChatAboveText.Y < 260) throw new InvalidOperationException("微信候选栏在原文下方时，译文不该盖住原文");
        var weChatOverText = Place(new Rect(80, 180, 360, 40), 180, 32, 6, work, new Rect(90, 236, 90, 24), false, true);
        if (weChatOverText.Y + 32 > 180) throw new InvalidOperationException("微信候选栏在原文上方时，译文也必须在上方");
    }

    private static Result PlaceWeChat(Rect candidate, int panelWidth, int panelHeight, int gap, Rect work, Rect? inputLine, bool forceAbove)
    {
        int x = Clamp((int)candidate.Left, (int)work.Left + 4, Math.Max((int)work.Left + 4, (int)work.Right - panelWidth - 4));
        int belowY = (int)candidate.Bottom + gap;
        bool fitsBelow = belowY + panelHeight <= work.Bottom - 4;
        bool textBelow = inputLine is { } line && line.Top + line.Height / 2 > candidate.Bottom - 4;
        // 候选栏浮在原文上方时，译文跟着放到上面。只有候选栏在原文下方、且下面还有空间时才放到下面。
        bool below = !forceAbove && !textBelow && fitsBelow;
        int y = below ? belowY : inputLine is { } above && above.Top + above.Height / 2 < candidate.Top ? (int)above.Top - gap - panelHeight : (int)candidate.Top - gap - panelHeight;
        if (!below && y < work.Top + 4) y = belowY;
        y = Clamp(y, (int)work.Top + 4, Math.Max((int)work.Top + 4, (int)work.Bottom - panelHeight - 4));
        return new Result(x, y);
    }

    private static int Above(Rect candidate, Rect? inputLine, int panelHeight, int gap)
    {
        // 微软拼音的原文贴在候选栏上沿。译文只要改到上面，就先空出这一行。
        int band = (int)Math.Clamp(candidate.Height, 24, 44);
        if (inputLine is { } line && line.Height is >= 8 and <= 96 && line.Top < candidate.Top + 8)
            band = Math.Max(band, (int)Math.Ceiling(candidate.Top - line.Top));
        return (int)candidate.Top - band - gap - panelHeight;
    }

    private static bool KeepMicrosoftLine(Rect candidate, Rect line)
    {
        if (line.Height is < 8 or > 96 || line.Width is < 0 or > 1200) return false;
        if (line.Right < candidate.Left - 80 || line.Left > candidate.Right + 240) return false;
        double mid = line.Top + line.Height / 2;
        return (mid < candidate.Top + 8 && candidate.Top - line.Bottom < 280) ||
            (mid > candidate.Bottom - 4 && line.Top - candidate.Bottom < 280);
    }

    private static bool Overlaps(int y, int height, Rect line) => y < line.Bottom && y + height > line.Top;
    private static int Clamp(int value, int minimum, int maximum) => Math.Min(maximum, Math.Max(minimum, value));
}
