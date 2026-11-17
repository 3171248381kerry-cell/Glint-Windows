namespace Glint.Windows.Services;

public enum TranslationMode { Smart, DictionaryFirst, ModelFirst }

public sealed record TranslationResult(string Text, string? PartOfSpeech = null, string Source = "词典", string? SpeakText = null);
