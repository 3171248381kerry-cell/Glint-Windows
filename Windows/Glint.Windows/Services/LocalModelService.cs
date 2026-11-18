using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Glint.Windows.Services;

/// <summary>Owns one local CTranslate2 worker for the selected translation direction.</summary>
public sealed class LocalModelService : IDisposable
{
    public const string RuntimeId = "opus-0.1.0-win-v1";
    public readonly record struct ModelTranslation(string Text, string? Reading);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object workerSync = new();
    private readonly ConcurrentDictionary<string, byte> failedTargets = new();
    private readonly string installationDirectory;
    private readonly string assetsDirectory;
    private readonly TimeSpan idleTimeout;
    private readonly Timer idleTimer;
    private (string Target, Process Process)? worker;
    private long lastRequestFinished;
    private string? selectedTarget;
    private int selectionRevision;
    private int requestId;
    private volatile bool disposed;
    private string? status;
    public bool IsAvailable => IsReady("en");
    public bool IsReady(string target) => RuntimeInformation.OSArchitecture == Architecture.X64 && !disposed && !failedTargets.ContainsKey(target)
        && target is "en" or "ja" or "ko"
        && File.Exists(Path.Combine(installationDirectory, "ready.json"))
        && File.Exists(Path.Combine(installationDirectory, "Python", "python.exe"))
        && File.Exists(ModelFile(target));
    public string Status => RuntimeInformation.OSArchitecture != Architecture.X64 ? "本地模型当前仅支持 Windows x64" : status ?? (IsAvailable ? "OPUS-MT 已就绪 · 本地运行" : "尚未安装 OPUS-MT");
    public string Availability => $"英语{(IsReady("en") ? "已就绪" : "未安装")} · 日语{(IsReady("ja") ? "已就绪" : "未安装")} · 韩语{(IsReady("ko") ? "已就绪" : "未安装")}";
    public string InstallationDirectory => installationDirectory;

    public LocalModelService() : this(TimeSpan.FromMinutes(1)) { }

    internal LocalModelService(TimeSpan idleTimeout)
    {
        installationDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint", "Offline", RuntimeId);
        assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Runtime");
        this.idleTimeout = idleTimeout;
        idleTimer = new Timer(_ => ReleaseIdleWorker());
    }

    internal int? WorkerProcessId
    {
        get { lock (workerSync) return worker?.Process.Id; }
    }

    internal void SelectTarget(string target)
    {
        lock (workerSync)
        {
            if (selectedTarget == target) return;
            selectedTarget = target;
            Interlocked.Increment(ref selectionRevision);
            if (worker is { } active && active.Target != target) StopWorker();
        }
    }

    public async Task<ModelTranslation?> TranslateAsync(string text, string target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var translation = await TranslateCoreAsync(text, target, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return translation;
    }

    private async Task<ModelTranslation?> TranslateCoreAsync(string text, string target, CancellationToken ct)
    {
        if (!IsReady(target) || text.Length is 0 or > 200) return null;
        await gate.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        int revision = Volatile.Read(ref selectionRevision);
        try
        {
            if (!IsReady(target)) return null;
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var process = await EnsureWorkerAsync(target, startup.Token, ct).ConfigureAwait(false);
            if (process is null || ct.IsCancellationRequested) return null;
            // Finish reading the submitted response even if its candidate goes stale.
            // The next request can reuse the warm worker and still gets its own response.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var id = ++requestId;
            var payload = JsonSerializer.Serialize(new { id, text, target });
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (line is null || line.Length > 16384) throw new InvalidDataException("Invalid worker response.");
            using var response = JsonDocument.Parse(line);
            if (!response.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                throw new InvalidDataException("Worker response revision mismatch.");
            if (!response.RootElement.TryGetProperty("text", out var translated)) return null;
            var output = translated.GetString()?.Trim();
            if (output is null || !Acceptable(target, output)) return null;
            string? reading = target == "ja" && response.RootElement.TryGetProperty("reading", out var readingValue) ? readingValue.GetString() : null;
            return new ModelTranslation(output, reading);
        }
        catch (OperationCanceledException)
        {
            StopWorker();
            status = "模型暂时不可用，请重试";
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            lock (workerSync)
            {
                StopWorker();
                if (!disposed && !ct.IsCancellationRequested && revision == selectionRevision && (selectedTarget is null || selectedTarget == target))
                {
                    failedTargets.TryAdd(target, 0);
                    status = "模型运行环境不可用，请重新安装模型";
                }
            }
            return null;
        }
        finally
        {
            lock (workerSync)
            {
                if (!disposed && worker is not null)
                {
                    lastRequestFinished = Stopwatch.GetTimestamp();
                    idleTimer.Change(idleTimeout, Timeout.InfiniteTimeSpan);
                }
            }
            gate.Release();
        }
    }

    private void ReleaseIdleWorker()
    {
        if (!gate.Wait(0)) return;
        try
        {
            lock (workerSync)
            {
                if (disposed || worker is null) return;
                var remaining = idleTimeout - Stopwatch.GetElapsedTime(lastRequestFinished);
                if (remaining <= TimeSpan.Zero) StopWorker();
                else idleTimer.Change(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), Timeout.InfiniteTimeSpan);
            }
        }
        catch (IOException) { }
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

    private async Task<Process?> EnsureWorkerAsync(string target, CancellationToken startup, CancellationToken request)
    {
        Process process;
        lock (workerSync)
        {
            if (disposed || request.IsCancellationRequested || (selectedTarget is not null && selectedTarget != target)) return null;
            if (worker is { } active && active.Target == target && !active.Process.HasExited) return active.Process;
            StopWorker();
            var info = new ProcessStartInfo(Path.Combine(installationDirectory, "Python", "python.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = installationDirectory
            };
            foreach (var arg in new[] { "-I", "-u", Path.Combine(assetsDirectory, "opus_worker.py"), installationDirectory, AppContext.BaseDirectory, target }) info.ArgumentList.Add(arg);
            info.Environment["HF_HUB_OFFLINE"] = "1";
            info.Environment["TRANSFORMERS_OFFLINE"] = "1";
            info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            process = Process.Start(info) ?? throw new IOException("Worker did not start.");
            worker = (target, process);
        }
        _ = DiscardErrorsAsync(process);
        var line = await process.StandardOutput.ReadLineAsync(startup).ConfigureAwait(false);
        if (line is null || line.Length > 1024) throw new InvalidDataException("Worker did not become ready.");
        using var response = JsonDocument.Parse(line);
        if (!response.RootElement.TryGetProperty("ready", out var ready) || !ready.GetBoolean()) throw new InvalidDataException("Worker is unavailable.");
        status = "OPUS-MT 已就绪 · 本地运行";
        return process;
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
            failedTargets.Clear();
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
        lock (workerSync)
        {
            if (worker is not { } active) return;
            worker = null;
            idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            using var process = active.Process;
            // Close the redirected handles we accessed. Process.Dispose leaves those streams open.
            using var input = process.StandardInput.BaseStream;
            using var output = process.StandardOutput;
            using var error = process.StandardError;
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    public void Dispose()
    {
        lock (workerSync)
        {
            if (disposed) return;
            disposed = true;
            StopWorker();
            idleTimer.Dispose();
        }
        // An active cancellation can still release this semaphore, so it lives until process exit.
    }
}
