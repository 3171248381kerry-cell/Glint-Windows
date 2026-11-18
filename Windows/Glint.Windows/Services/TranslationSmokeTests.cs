using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Glint.Windows.Services;

/// <summary>Runs against the real bundled databases, not duplicated query logic or mocks.</summary>
public static class TranslationSmokeTests
{
    public sealed record ValidationReport(int DictionaryCases, int Top1, int Top3, int BlockedCases, int ForbiddenChecks, bool OfflineModelValidated);
    public static ValidationReport? LastReport { get; private set; }

    public static async Task RunAsync(TranslationService service)
    {
        Require(service.IsAvailable, "Dictionary resource hashes and schemas");
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "dictionary-benchmark.json");
        using var benchmark = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var count = 0; var top1 = 0; var top3 = 0; var blocked = 0; var forbiddenChecks = 0;
        var failures = new List<string>();
        foreach (var entry in benchmark.RootElement.EnumerateArray())
        {
            var category = entry.GetProperty("category").GetString();
            // Upstream reports phrases and model-only modern senses separately from dictionary gates.
            if (category is "phrase" or "model") continue;
            var text = entry.GetProperty("text").GetString()!;
            var result = await service.LookupDictionaryOnlyAsync(text);
            if (entry.TryGetProperty("forbidden", out var forbidden))
            {
                var forbiddenKeys = forbidden.EnumerateArray().Select(v => Key(v.GetString()!)).ToHashSet();
                forbiddenChecks += forbiddenKeys.Count;
                Require(!result.Any(v => forbiddenKeys.Contains(Key(v.Text))), "Forbidden dictionary candidate");
            }
            var expected = entry.GetProperty("expected").EnumerateArray().Select(v => Key(v.GetString()!)).ToHashSet();
            if (category == "blocked") { Require(result.Count == 0, "Blocked dictionary candidate"); blocked++; continue; }
            count++;
            if (result.Count > 0 && expected.Contains(Key(result[0].Text))) top1++;
            else failures.Add(text);
            if (result.Take(3).Any(v => expected.Contains(Key(v.Text)))) top3++;
        }
        LastReport = new(count, top1, top3, blocked, forbiddenChecks, false);
        Require(count >= 100, "Complete upstream dictionary benchmark is bundled");
        Require((double)top1 / count >= .98 && (double)top3 / count >= .98,
            $"Dictionary benchmark Top1={top1}/{count}, Top3={top3}/{count}; failures: {string.Join(", ", failures)}");
        var japaneseReady = service.Model.IsReady("ja");
        var japanese = await service.TranslateAsync("你好", "ja");
        Require(japanese.All(x => JapaneseDisplay.IsUsable(x.Text) && !x.Text.Contains("hello", StringComparison.OrdinalIgnoreCase)), "Japanese does not silently become English");
        Require(!japaneseReady || japanese.Count > 0, "Installed Japanese model produces a translation");
        var koreanReady = service.Model.IsReady("ko");
        var korean = await service.TranslateAsync("你好", "ko");
        Require(korean.All(x => x.Text.Any(c => c is >= '\uAC00' and <= '\uD7A3')), "Korean does not silently become English");
        Require(!koreanReady || korean.Count > 0, "Installed Korean model produces a translation");
        Require((await service.TranslateAsync("你好", "fr")).Count == 0, "Unsupported target does not silently become English");
        Require((await service.TranslateAsync("hello")).Count == 0, "Non-Han text is rejected");
        var apple = await service.TranslateAsync("苹果", "en", TranslationMode.DictionaryFirst);
        Require(apple.Any(x => x.Text.Equals("apple", StringComparison.OrdinalIgnoreCase) && x.PartOfSpeech?.Contains("n.") == true), "Real English part of speech data");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = false;
        try { await service.TranslateAsync("你好", ct: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Cancellation invalidates even cached requests");
        if (service.Model.IsAvailable)
        {
            service.Model.SelectTarget("ja");
            Require(await service.Model.TranslateAsync("你好", "en") is null, "The worker rejects a language other than the selected target");
            service.Model.SelectTarget("en");
            var sentence = await service.TranslateAsync("请帮我检查这个文件", "en", TranslationMode.ModelFirst);
            Require(sentence.Any(x => x.Source == "OPUS-MT" && x.Text.Contains("file", StringComparison.OrdinalIgnoreCase)), "Installed model performs real offline inference");
            await CheckIdleWorkerAsync();
            LastReport = LastReport with { OfflineModelValidated = true };
        }
    }

    private static async Task CheckIdleWorkerAsync()
    {
        using var model = new LocalModelService(TimeSpan.FromSeconds(2));
        const string sentence = "请帮我检查这个文件";
        Require(await model.TranslateAsync(sentence, "en") is not null, "Worker starts on demand");
        int pid = model.WorkerProcessId ?? throw new InvalidOperationException("Missing model worker process");
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        Require(await model.TranslateAsync(sentence, "en") is not null && model.WorkerProcessId == pid, "Recent inference reuses the warm worker");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
        Require(model.WorkerProcessId is null && model.IsReady("en"), "Idle worker exits while the installed model remains ready");
        Require(await model.TranslateAsync(sentence, "en") is not null, "Model translates again after idle release");
    }

    private static string Key(string value)
    {
        var text = Regex.Replace(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"^(?:\([^)]*\)\s*)+", "");
        text = Regex.Replace(text, @"[^a-z0-9']+", " ").Trim();
        text = Regex.Replace(text, @"\s+", " ");
        foreach (var prefix in new[] { "to ", "the ", "a ", "an ", "be ", "of " })
            if (text.StartsWith(prefix)) { text = text[prefix.Length..]; break; }
        return text;
    }
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException("Translation validation failed: " + detail);
    }
}
