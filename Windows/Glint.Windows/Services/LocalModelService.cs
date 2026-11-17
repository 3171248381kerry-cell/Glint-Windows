using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Glint.Windows.Services;

/// <summary>Owns a private, persistent CTranslate2 worker; candidate IPC never reaches the network.</summary>
public sealed class LocalModelService : IDisposable
{
    public const string RuntimeId = "opus-0.1.0-win-v1";
    public readonly record struct ModelTranslation(string Text, string? Reading);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string installationDirectory;
    private readonly string assetsDirectory;
    private Process? worker;
    private int requestId;
    private bool disposed;
    private bool failed;
    private string? status;
    public bool IsAvailable => IsReady("en");
    public bool IsReady(string target) => RuntimeInformation.OSArchitecture == Architecture.X64 && !disposed && !failed
        && target is "en" or "ja" or "ko"
        && File.Exists(Path.Combine(installationDirectory, "ready.json"))
        && File.Exists(Path.Combine(installationDirectory, "Python", "python.exe"))
        && File.Exists(ModelFile(target));
    public string Status => RuntimeInformation.OSArchitecture != Architecture.X64 ? "本地模型当前仅支持 Windows x64" : status ?? (IsAvailable ? "OPUS-MT 已就绪 · 本地运行" : "尚未安装 OPUS-MT");
    public string Availability => $"英语{(IsReady("en") ? "已就绪" : "未安装")} · 日语{(IsReady("ja") ? "已就绪" : "未安装")} · 韩语{(IsReady("ko") ? "已就绪" : "未安装")}";
    public string CacheIdentity => string.Join(",", new[] { "en", "ja", "ko" }.Select(target => target + ":" + (IsReady(target) ? RuntimeId : "none")));
    public string InstallationDirectory => installationDirectory;

    public LocalModelService()
    {
        installationDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint", "Offline", RuntimeId);
        assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Runtime");
    }

    public async Task<ModelTranslation?> TranslateAsync(string text, string target, CancellationToken ct = default)
    {
        if (!IsReady(target) || text.Length is 0 or > 200) return null;
        await gate.WaitAsync(ct);
        try
        {
            if (!IsReady(target)) return null;
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await EnsureWorkerAsync(startup.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var id = ++requestId;
            var payload = JsonSerializer.Serialize(new { id, text, target });
            await worker!.StandardInput.WriteLineAsync(payload.AsMemory(), deadline.Token);
            await worker.StandardInput.FlushAsync(deadline.Token);
            var line = await worker.StandardOutput.ReadLineAsync(deadline.Token);
            if (line is null || line.Length > 16384) throw new InvalidDataException("Invalid worker response.");
            using var response = JsonDocument.Parse(line);
            if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                throw new InvalidDataException("Worker response revision mismatch.");
            if (!response.RootElement.TryGetProperty("text", out var translated)) return null;
            ct.ThrowIfCancellationRequested();
            var output = translated.GetString()?.Trim();
            if (output is null || !Acceptable(target, output)) return null;
            string? reading = target == "ja" && response.RootElement.TryGetProperty("reading", out var readingValue) ? readingValue.GetString() : null;
            return new ModelTranslation(output, reading);
        }
        catch (OperationCanceledException)
        {
            StopWorker();
            status = "模型暂时不可用，请重试";
            if (ct.IsCancellationRequested) throw;
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            StopWorker();
            failed = true;
            status = "模型运行环境不可用，请重新安装模型";
            return null;
        }
        finally { gate.Release(); }
    }

    private static bool Acceptable(string target, string output)
    {
        if (output.Length is 0 or > 1000) return false;
        int han = output.Count(DictionarySurface.IsHan);
        int kana = output.Count(c => c is >= '\u3040' and <= '\u30FF');
        int hangul = output.Count(c => c is >= '\uAC00' and <= '\uD7A3');
        int latin = output.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return target switch
        {
            "en" => han == 0 && kana == 0 && hangul == 0 && latin > 0,
            "ja" => hangul == 0 && latin <= 2 && han + kana > 0,
            "ko" => kana == 0 && hangul > 0 && latin <= 2,
            _ => false
        };
    }

    private string ModelFile(string target) => Path.Combine(installationDirectory, target switch
    {
        "ja" => "mt-opus-zh-ja",
        "ko" => "mt-opus-zh-ko",
        _ => "mt-opus-zh-en"
    }, "model", "model.bin");

    private async Task EnsureWorkerAsync(CancellationToken ct)
    {
        if (worker is { HasExited: false }) return;
        StopWorker();
        var info = new ProcessStartInfo(Path.Combine(installationDirectory, "Python", "python.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = installationDirectory
        };
        foreach (var arg in new[] { "-I", "-u", Path.Combine(assetsDirectory, "opus_worker.py"), installationDirectory, AppContext.BaseDirectory }) info.ArgumentList.Add(arg);
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        worker = Process.Start(info) ?? throw new IOException("Worker did not start.");
        _ = DiscardErrorsAsync(worker);
        var line = await worker.StandardOutput.ReadLineAsync(ct);
        if (line is null || line.Length > 1024) throw new InvalidDataException("Worker did not become ready.");
        using var response = JsonDocument.Parse(line);
        if (!response.RootElement.TryGetProperty("ready", out var ready) || !ready.GetBoolean()) throw new InvalidDataException("Worker is unavailable.");
        status = "OPUS-MT 已就绪 · 本地运行";
    }

    private static async Task DiscardErrorsAsync(Process process)
    {
        try { while (await process.StandardError.ReadLineAsync() is not null) { } }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    public async Task InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        Process? installer = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (RuntimeInformation.OSArchitecture != Architecture.X64) throw new PlatformNotSupportedException("本地模型当前仅支持 Windows x64。");
            StopWorker();
            failed = false;
            status = "正在准备离线翻译";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(assetsDirectory, "install-model.ps1") }) info.ArgumentList.Add(arg);
            installer = Process.Start(info) ?? throw new IOException("Installer did not start.");
            var errorTask = installer.StandardError.ReadToEndAsync(ct);
            while (await installer.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length is > 0 and < 512) { status = line; progress?.Report(line); }
            }
            await installer.WaitForExitAsync(ct);
            _ = await errorTask;
            if (installer.ExitCode != 0 || !IsAvailable) throw new InvalidOperationException("离线模型安装失败，请检查网络后重试。若环境校验失败，请安装 Microsoft Visual C++ 2015–2022 x64 运行库。");
            await InstallLanguageAsync("ja", progress, ct);
            await InstallLanguageAsync("ko", progress, ct);
            status = "OPUS-MT 已就绪 · 本地运行";
            progress?.Report(status);
        }
        catch
        {
            if (installer is { HasExited: false }) { try { installer.Kill(true); } catch (InvalidOperationException) { } }
            status = IsAvailable ? "OPUS-MT 已就绪 · 本地运行" : "安装未完成，可重试";
            throw;
        }
        finally { installer?.Dispose(); gate.Release(); }
    }

    private async Task InstallLanguageAsync(string target, IProgress<string>? progress, CancellationToken ct)
    {
        if (IsReady(target)) return;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(assetsDirectory, "install-language.ps1"), "-Language", target }) info.ArgumentList.Add(arg);
        using var installer = Process.Start(info) ?? throw new IOException("Installer did not start.");
        while (await installer.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (line.Length is > 0 and < 512) { status = line; progress?.Report(line); }
        }
        await installer.WaitForExitAsync(ct);
        if (installer.ExitCode != 0 || !IsReady(target)) throw new InvalidOperationException("离线模型安装失败，请检查网络后重试。");
    }

    private void StopWorker()
    {
        var process = Interlocked.Exchange(ref worker, null);
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }

    public void Dispose()
    {
        disposed = true;
        StopWorker();
        // An active cancellation can still release this semaphore, so it lives until process exit.
    }
}
