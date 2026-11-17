using System.Runtime.InteropServices;

namespace Glint.Windows.Services;

/// <summary>Windows installed voices only, with asynchronous playback and no network access.</summary>
public sealed class SpeechService : IDisposable
{
    private dynamic? _voice;
    private readonly Dictionary<string, (object Token, int Language)> _tokens = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> VoiceNames => NamesFor("en");
    public IReadOnlyList<string> NamesFor(string target)
    {
        int primary = Primary(target);
        return _tokens.Where(x => x.Value.Language == primary).Select(x => x.Key).ToArray();
    }
    public static int Primary(string target) => target switch { "ja" => 0x11, "ko" => 0x12, _ => 0x09 };
    public SpeechService()
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is not null)
            {
                _voice = Activator.CreateInstance(type);
                dynamic voices = _voice!.GetVoices();
                for (int i = 0; i < voices.Count; i++)
                {
                    dynamic token = voices.Item(i);
                    string language = token.GetAttribute("Language");
                    int primary = language.Split(';').Select(x => int.TryParse(x, System.Globalization.NumberStyles.HexNumber, null, out int lcid) ? lcid & 0x3ff : 0).FirstOrDefault(x => x is 0x09 or 0x11 or 0x12);
                    if (primary == 0) continue;
                    _tokens[token.GetDescription()] = (token, primary);
                }
            }
        }
        catch (COMException) { _voice = null; }
    }
    public void Speak(string text, string voice, int rate, string target = "en")
    {
        if (_voice is null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            int primary = Primary(target);
            object? token = null;
            if (_tokens.TryGetValue(voice, out var selected) && selected.Language == primary) token = selected.Token;
            else
            {
                foreach (var candidate in _tokens.Values)
                    if (candidate.Language == primary) { token = candidate.Token; break; }
            }
            if (token is null) return;
            _voice.Voice = token;
            _voice.Rate = Math.Clamp(rate, -5, 5);
            _voice.Speak(text, 1 | 2 | 16); // async, purge prior speech, plain text (not SSML)
        }
        catch (COMException) { }
    }
    public void Stop() { try { _voice?.Speak("", 1 | 2 | 16); } catch (COMException) { } }
    public void Dispose()
    {
        Stop();
        if (_voice is not null) Marshal.FinalReleaseComObject((object)_voice);
        _voice = null;
        foreach (var token in _tokens.Values) Marshal.FinalReleaseComObject(token.Token);
        _tokens.Clear();
    }
}
