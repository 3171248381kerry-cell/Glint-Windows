using System.Security.Cryptography;

namespace Glint.Windows.Services;

/// <summary>Offline port of the authoritative CC-CEDICT → ECDICT fallback chain.</summary>
public sealed class TranslationService : IDisposable
{
    private sealed record Ranking(string English, int Frequency, int Collins, int Oxford, bool ExamTagged);
    private sealed record Option(string Text, double Score, string Source = "词典");
    private readonly object sync = new();
    private readonly ReadOnlySqlite? cedict;
    private readonly ReadOnlySqlite? ecdict;
    private readonly Dictionary<string, LinkedListNode<(string Key, IReadOnlyList<TranslationResult> Value)>> cache = [];
    private readonly LinkedList<(string Key, IReadOnlyList<TranslationResult> Value)> lru = [];
    private bool disposed;
    public LocalModelService Model { get; } = new();
    public bool IsAvailable => cedict is not null && ecdict is not null;
    public string Status => IsAvailable ? "内置词典已就绪" : "内置词典缺失或校验失败，请重新解压完整应用";

    public TranslationService(string? dataDirectory = null)
    {
        var root = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "Data");
        try
        {
            var cedictPath = FindDictionary(root, "dict-cedict", "cedict.sqlite3");
            var ecdictPath = FindDictionary(root, "dict-ecdict", "ecdict_rank.sqlite3");
            Verify(cedictPath, "6ceae80b10e4003913a8435cea33988246a4e94e09260f53af0e1f2e5e46cf3b");
            Verify(ecdictPath, "54ed081d80fb17c8ceca469acdd3c336284e0e07b8760c850b5cb1e403762d0e");
            cedict = new ReadOnlySqlite(cedictPath);
            ecdict = new ReadOnlySqlite(ecdictPath);
            _ = cedict.Query("SELECT gloss FROM senses WHERE entry_id = ? LIMIT 1", "0");
            _ = ecdict.Query("SELECT parts_of_speech FROM english_parts_of_speech WHERE english_key = ?", "hello");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            cedict?.Dispose(); ecdict?.Dispose(); cedict = null; ecdict = null;
        }
    }

    public async Task<IReadOnlyList<TranslationResult>> TranslateAsync(string text, string target = "en", TranslationMode mode = TranslationMode.Smart, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        text = text.Trim();
        if (disposed || target is not ("en" or "ja" or "ko") || text.Length is 0 or > 200 || text == "你号" || !text.Any(DictionarySurface.IsHan)) return [];
        var modelReady = Model.IsReady(target);
        var modelIdentity = modelReady ? LocalModelService.RuntimeId : "none";
        var key = $"cedict-2026.10.3:ecdict-2026.10.20.3:{modelIdentity}:{target}:{mode}:{text}";
        lock (sync)
        {
            if (cache.TryGetValue(key, out var node)) { lru.Remove(node); lru.AddFirst(node); return node.Value.Value; }
        }
        if (target != "en")
            return modelReady ? await TranslateModelAsync(text, target, key, ct).ConfigureAwait(false) : [];

        var sentence = text.Length >= 9 || text.Any(c => "，。！？；：,.!?;:".Contains(c));
        List<Option> options;
        if (sentence && modelReady)
        {
            var modelText = await ModelTextAsync(text, ct).ConfigureAwait(false);
            options = modelText is null
                ? await Task.Run(() => LookupDictionary(text, ct), ct).ConfigureAwait(false)
                : [new Option(modelText, 1, "OPUS-MT")];
        }
        else
        {
            var dictionaryTask = Task.Run(() => LookupDictionary(text, ct), ct);
            if (mode == TranslationMode.ModelFirst && modelReady)
            {
                var modelTask = ModelTextAsync(text, ct);
                await Task.WhenAll(dictionaryTask, modelTask).ConfigureAwait(false);
                var dictionary = await dictionaryTask.ConfigureAwait(false);
                var modelText = await modelTask.ConfigureAwait(false);
                options = modelText is null ? dictionary
                    : [new Option(modelText, 2, "OPUS-MT"), .. dictionary.Select((o, i) => o with { Score = 1 - i * .001 })];
            }
            else
            {
                // For words and phrases a dictionary hit can go straight to the HUD.
                options = await dictionaryTask.ConfigureAwait(false);
                if (options.Count == 0 && modelReady && await ModelTextAsync(text, ct).ConfigureAwait(false) is { } modelText)
                    options.Add(new Option(modelText, 1, "OPUS-MT"));
            }
        }
        ct.ThrowIfCancellationRequested();
        return Remember(key, options, null);
    }

    private async Task<IReadOnlyList<TranslationResult>> TranslateModelAsync(string text, string target, string key, CancellationToken ct)
    {
        var output = await Model.TranslateAsync(text, target, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (output is not { } translated) return [];
        var shown = target == "ja" ? JapaneseDisplay.Format(translated.Text, translated.Reading) : translated.Text;
        var speak = target == "ja" ? JapaneseDisplay.Pronunciation(translated.Text, translated.Reading) : translated.Text;
        if (target == "ja" && !JapaneseDisplay.IsUsable(shown)) return [];
        if (target == "ko" && !shown.Any(c => c is >= '\uAC00' and <= '\uD7A3')) return [];
        return Remember(key, [new Option(shown, 1, "OPUS-MT")], speak);
    }

    private async Task<string?> ModelTextAsync(string text, CancellationToken ct)
    {
        var output = await Model.TranslateAsync(text, "en", ct).ConfigureAwait(false);
        return output is { } translated ? translated.Text : null;
    }

    public void ClearCache()
    {
        lock (sync) { cache.Clear(); lru.Clear(); }
    }

    private IReadOnlyList<TranslationResult> Remember(string key, List<Option> options, string? speak)
    {
        if (options.Count == 0) return [];
        lock (sync)
        {
            if (disposed) return [];
            var result = options.OrderByDescending(o => o.Score).ThenBy(o => o.Text, StringComparer.Ordinal)
                .DistinctBy(o => DictionarySurface.DedupeKey(o.Text)).Take(8)
                .Select((o, index) => new TranslationResult(o.Text, PartOfSpeech(o.Text), o.Source, index == 0 ? speak : null)).ToArray();
            if (cache.TryGetValue(key, out var existing)) { lru.Remove(existing); cache.Remove(key); }
            var node = lru.AddFirst((key, result)); cache[key] = node;
            if (cache.Count > 256) { var last = lru.Last!; cache.Remove(last.Value.Key); lru.RemoveLast(); }
            return result;
        }
    }

    // The upstream dictionary quality gate evaluates dictionary coverage separately from model fallback.
    internal Task<IReadOnlyList<TranslationResult>> LookupDictionaryOnlyAsync(string text, CancellationToken ct = default) => Task.Run<IReadOnlyList<TranslationResult>>(() =>
    {
        ct.ThrowIfCancellationRequested();
        text = text.Trim();
        if (disposed || text.Length is 0 or > 200 || text == "你号" || !text.Any(DictionarySurface.IsHan)) return [];
        var options = LookupDictionary(text, ct);
        lock (sync)
        {
            if (disposed) return [];
            return options.Take(8).Select(o => new TranslationResult(o.Text, PartOfSpeech(o.Text), o.Source)).ToArray();
        }
    }, ct);

    private List<Option> LookupDictionary(string text, CancellationToken ct)
    {
        lock (sync)
        {
            if (disposed || !IsAvailable) return [];
            ct.ThrowIfCancellationRequested();
            var rankings = ecdict!.Query("SELECT english, frequency, collins, oxford, exam_tag FROM ranks WHERE term = ? ORDER BY frequency ASC, collins DESC, oxford DESC LIMIT 12", text)
                .Select(r => new Ranking(r[0], int.Parse(r[1]), int.Parse(r[2]), int.Parse(r[3]), r[4] != "0")).ToList();
            var usableRankings = rankings.Where(r => (r.Frequency <= 100000 || r.ExamTagged) && r.English.Length > 1).ToList();
            var byEnglish = usableRankings.Select((r, i) => (Ranking: r, Index: i)).DistinctBy(x => DictionarySurface.NormalizeEnglish(x.Ranking.English)).ToDictionary(x => DictionarySurface.NormalizeEnglish(x.Ranking.English));
            var options = new List<Option>();
            var surfaces = new List<string>();
            foreach (var row in cedict!.Query("SELECT senses.gloss, terms.entry_id, senses.sense_order FROM terms JOIN senses ON senses.entry_id = terms.entry_id WHERE terms.term = ? ORDER BY terms.entry_id, senses.sense_order LIMIT 64", text))
            {
                ct.ThrowIfCancellationRequested();
                var penalty = int.Parse(row[1]) * .00000001 + int.Parse(row[2]) * .03;
                var glosses = row[0].Split([';', '/']).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("CL:")).Select(DictionarySurface.LeadingQuotedHeadword).ToArray();
                for (var i = 0; i < glosses.Length; i++)
                {
                    var compact = DictionarySurface.Normalize(glosses[i]);
                    if (compact is null) continue;
                    var normalized = DictionarySurface.NormalizeEnglish(compact);
                    if (normalized.Length == 0) continue;
                    surfaces.Add(compact);
                    var score = 1 - i * .06 - penalty;
                    if (byEnglish.TryGetValue(normalized, out var entry))
                    {
                        score += FrequencyBoost(entry.Ranking) + entry.Ranking.Collins * .012 + entry.Ranking.Oxford * .008;
                        if (entry.Index > 0 && !string.Equals(compact, DictionarySurface.Normalize(entry.Ranking.English), StringComparison.OrdinalIgnoreCase))
                            score -= Math.Min(.20, entry.Index * .04);
                    }
                    if (compact.Contains(' ')) score -= .05;
                    options.Add(new Option(compact, score));
                }
            }
            if (options.Count > 0)
            {
                var represented = surfaces.Select(DictionarySurface.NormalizeEnglish).ToHashSet();
                var sourceCount = options.Count;
                var sourceFloor = options.Min(o => o.Score);
                var supplements = 0;
                foreach (var (ranking, i) in usableRankings.Take(6).Select((r, i) => (r, i)))
                {
                    var compact = DictionarySurface.Normalize(ranking.English);
                    if (compact is null || represented.Contains(DictionarySurface.NormalizeEnglish(compact))) continue;
                    var sourceSupported = surfaces.Any(s => DictionarySurface.PhraseTokens(s).Contains(DictionarySurface.PhraseTokens(compact)));
                    var tagSupported = ranking.ExamTagged && sourceCount + supplements < 3;
                    if (!sourceSupported && !tagSupported) continue;
                    var score = .82 - i * .035 + FrequencyBoost(ranking) + ranking.Collins * .012 + ranking.Oxford * .008 + (compact.Contains(' ') ? -.05 : .04);
                    if (!sourceSupported) { score = Math.Min(score, sourceFloor - .005 - supplements * .005); supplements++; }
                    options.Add(new Option(compact, score));
                }
            }
            var authoritative = Displayable(options);
            if (authoritative.Count > 0) return authoritative;
            return Displayable(rankings.Where(r => r.Frequency <= 100000 && r.English.Length > 1).Take(3).Select((r, i) => new Option(r.English, .70 - i * .05, "ECDICT")).ToList());
        }
    }

    private static double FrequencyBoost(Ranking ranking) => Math.Max(0, .25 - Math.Log10(Math.Max(1, ranking.Frequency)) * .04);
    private static List<Option> Displayable(List<Option> values) => values.SelectMany(o => o.Text.Split(';').Select(DictionarySurface.Normalize).Where(s => s is not null && DictionarySurface.IsDisplayable(s)).Select((s, i) => o with { Text = s!, Score = o.Score + .2 - DictionarySurface.Penalty(s!) - i * .01 })).OrderByDescending(o => o.Score).ThenBy(o => o.Text, StringComparer.Ordinal).DistinctBy(o => DictionarySurface.DedupeKey(o.Text)).ToList();

    private string? PartOfSpeech(string value)
    {
        if (ecdict is null || value.Any(char.IsWhiteSpace) || !value.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') || value.Any(DictionarySurface.IsHan)) return null;
        var row = ecdict.Query("SELECT parts_of_speech FROM english_parts_of_speech WHERE english_key = ?", value.Normalize().ToLowerInvariant()).FirstOrDefault();
        if (row is null) return null;
        var names = row[0].Split('/').ToHashSet();
        var mapping = new (string Name, string Abbreviation)[] { ("noun", "n."), ("verb", "v."), ("adjective", "adj."), ("adverb", "adv."), ("preposition", "prep."), ("pronoun", "pron."), ("conjunction", "conj."), ("determiner", "det."), ("numeral", "num."), ("interjection", "interj.") };
        var parts = mapping.Where(x => names.Contains(x.Name) || names.Contains(x.Abbreviation)).Take(2).Select(x => x.Abbreviation).ToArray();
        return parts.Length > 0 ? string.Join(" / ", parts) : null;
    }

    private static string FindDictionary(string root, string folder, string name) => File.Exists(Path.Combine(root, folder, name)) ? Path.Combine(root, folder, name) : Path.Combine(root, name);
    private static void Verify(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bundled dictionary checksum failed.");
    }

    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; cedict?.Dispose(); ecdict?.Dispose(); cache.Clear(); lru.Clear(); }
        Model.Dispose();
    }
}
