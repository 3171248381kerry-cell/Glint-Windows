using System.Text.RegularExpressions;

namespace Glint.Windows.Services;

/// <summary>Port of GlintCore DictionarySurface and EnglishWordNormalizer.</summary>
internal static class DictionarySurface
{
    private static readonly string[] Prefixes = ["to ", "the ", "a ", "an ", "be ", "of "];
    private static readonly string[] ExplanationPrefixes = ["degree of ", "emergency number for ", "fig. ", "lit. ", "name of ", "nickname of ", "one who ", "person who ", "refers to ", "someone who ", "system where ", "used as ", "used for ", "used in "];
    private static readonly string[] ExplanationFragments = [" also called ", " also known as ", " established in ", " founded in ", " i.e. ", " located in ", " namely "];
    private static readonly HashSet<string> Abbreviations = ["co.", "corp.", "dr.", "etc.", "inc.", "jr.", "ltd.", "mr.", "mrs.", "ms.", "sr.", "st."];
    private static readonly HashSet<string> AppositiveNouns = ["abbreviation", "acronym", "actor", "actress", "agency", "animal", "app", "application", "artist", "author", "band", "bank", "belief", "bird", "businessman", "businesswoman", "capital", "character", "chemical", "city", "club", "code", "committee", "company", "compound", "concept", "condition", "corporation", "country", "county", "currency", "deity", "device", "dialect", "dish", "disease", "district", "doctrine", "drink", "emperor", "festival", "film", "fish", "food", "game", "gang", "general", "god", "goddess", "group", "insect", "institution", "instrument", "island", "islands", "king", "lake", "language", "leader", "machine", "manga", "mark", "mathematician", "missionary", "monk", "movement", "municipality", "musician", "name", "novel", "number", "officer", "organization", "organisation", "painter", "party", "peninsula", "philosopher", "physicist", "plant", "poet", "politician", "president", "prefecture", "priest", "province", "queen", "region", "revolutionary", "river", "ruler", "sauce", "scholar", "scientist", "sea", "series", "singer", "soldier", "species", "state", "strait", "symbol", "syndrome", "system", "team", "term", "territory", "theory", "title", "tool", "town", "tree", "university", "village", "volcano", "website", "writer"];
    private static readonly HashSet<string> Nationalities = ["american", "asian", "australian", "austrian", "belgian", "british", "byzantine", "canadian", "chinese", "dutch", "english", "european", "french", "german", "greek", "indian", "iranian", "iraqi", "italian", "japanese", "korean", "ottoman", "portuguese", "roman", "russian", "spanish", "swedish", "swiss", "taiwanese", "turkish", "uk", "us", "usa"];

    public static string? Normalize(string text)
    {
        var value = text.Trim();
        while (value.IndexOf('(') is var start && start >= 0)
        {
            var end = value.IndexOf(')', start);
            value = (end < 0 ? value[..start] : value.Remove(start, end - start + 1)).Trim();
        }
        value = Regex.Replace(value, @"\s+", " ");
        foreach (var prefix in Prefixes)
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { value = value[prefix.Length..]; break; }
        value = TrimPunctuation(value);
        if (value.EndsWith('.'))
        {
            var word = value.Split(' ').Last().ToLowerInvariant();
            if (!Abbreviations.Contains(word) && !(word.Length == 2 && char.IsLetter(word[0])) && !(word.Length >= 4 && word.Count(c => c == '.') >= 2))
                value = TrimPunctuation(value[..^1]);
        }
        var lower = value.ToLowerInvariant();
        if (value.Length == 0 || value.Any(IsHan) || new[] { "see ", "variant of ", "old variant ", "abbr. for ", "taiwan pr." }.Any(lower.StartsWith)) return null;
        return value;
    }

    private static string TrimPunctuation(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && (char.IsWhiteSpace(text[start]) || (char.IsPunctuation(text[start]) && text[start] != '.'))) start++;
        while (end > start && (char.IsWhiteSpace(text[end - 1]) || (char.IsPunctuation(text[end - 1]) && text[end - 1] != '.'))) end--;
        return text[start..end];
    }

    public static bool IsDisplayable(string value)
    {
        var lower = value.ToLowerInvariant();
        return value.Length <= 48 && value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8
            && !ExplanationPrefixes.Any(lower.StartsWith)
            && !ExplanationFragments.Any(fragment => (" " + lower + " ").Contains(fragment))
            && !HasExplanatoryAppositive(lower);
    }

    private static bool HasExplanatoryAppositive(string value)
    {
        var comma = value.IndexOf(',');
        if (comma < 0) return false;
        var wordCount = value[..comma].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var tokens = Regex.Split(value[(comma + 1)..], @"[^\p{L}\p{N}]+").Where(t => t.Length > 0).SkipWhile(t => t is "a" or "an" or "the").ToArray();
        if (tokens.Length == 0) return false;
        if (tokens[0] is "aka" or "formerly" or "located") return true;
        if (tokens.Length > 1 && ((tokens[0] == "one" && tokens[1] == "of") || (tokens[0] == "also" && tokens[1] is "called" or "known"))) return true;
        if (wordCount > 1 && tokens.Take(4).Any(AppositiveNouns.Contains)) return true;
        if (AppositiveNouns.Contains(tokens[0]))
            return wordCount > 1 || (tokens.Length > 1 && tokens[1] is "at" or "from" or "in" or "of" or "on" or "with");
        return Nationalities.Contains(tokens[0]) && tokens.Skip(1).Any(AppositiveNouns.Contains);
    }

    public static double Penalty(string text) => Math.Min(.20, Math.Max(0, text.Split(' ').Length - 3) * .04)
        + Math.Min(.12, Math.Max(0, text.Length - 28) * .005)
        + (text.StartsWith("surname ", StringComparison.OrdinalIgnoreCase) ? .25 : 0);

    public static string NormalizeEnglish(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        foreach (var prefix in Prefixes)
            if (value.StartsWith(prefix)) { value = value[prefix.Length..]; break; }
        value = string.Concat(value.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)));
        if (value.Length > 3 && value.EndsWith('s')) value = value[..^1];
        return value.Trim();
    }

    public static string DedupeKey(string text) => string.Concat(text.ToLowerInvariant().Where(char.IsLetterOrDigit));
    public static bool IsHan(char c) => c is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF';
    public static string PhraseTokens(string text) => " " + string.Join(" ", Regex.Split(text.ToLowerInvariant(), @"[^\p{L}]+").Where(t => t.Length > 0)) + " ";

    public static string LeadingQuotedHeadword(string text)
    {
        var rest = text;
        while (rest.StartsWith('('))
        {
            var end = rest.IndexOf(')');
            if (end < 0) return text;
            rest = rest[(end + 1)..].Trim();
        }
        if (rest.Length < 2 || rest[0] is not ('"' or '“')) return text;
        var closing = rest.IndexOf(rest[0] == '“' ? '”' : '"', 1);
        return closing > 1 ? rest[1..closing].Trim() : text;
    }
}
