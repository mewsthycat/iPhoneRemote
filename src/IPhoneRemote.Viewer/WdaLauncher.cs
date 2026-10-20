using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace IPhoneRemote.Viewer;

/// <summary>
/// Brings WebDriverAgent up without the setup script: iOS 17+ tunnel, developer disk image,
/// then `ios runwda`. Processes it starts are stopped again when the viewer closes.
/// Uses tools\ from the repo (go-ios and the developer-image cache from setup-wda.ps1).
/// </summary>
public sealed class WdaLauncher(AppSettings settings) : IDisposable
{
    private readonly string _tools = Path.Combine(FindRepoRoot(), "tools");
    private Process? _tunnel;
    private Process? _runwda;
    private readonly List<StreamWriter> _logs = [];

    private string Ios => Path.Combine(_tools, "go-ios", "ios.exe");
    private string LogDir => Path.Combine(_tools, "logs");

    public string RunWdaLogPath => Path.Combine(LogDir, "runwda.log");

    /// <summary>The repo root, baked in at build time (see the csproj), overridable via IPHONEREMOTE_ROOT.</summary>
    public static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("IPHONEREMOTE_ROOT");
        if (!string.IsNullOrEmpty(env)) return env;
        var baked = typeof(WdaLauncher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
        return Path.GetFullPath(baked ?? AppContext.BaseDirectory);
    }

    public async Task StartAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!File.Exists(Ios))
            throw new InvalidOperationException($"go-ios is missing ({Ios}). Run scripts\\setup-wda.ps1 -Stage tools once.");
        Directory.CreateDirectory(LogDir);

        progress.Report("Opening secure tunnel…");
        await EnsureTunnelAsync(ct);

        progress.Report("Mounting developer image…");
        await EnsureImageAsync(ct);

        progress.Report("Launching WebDriverAgent…");
        await StartWdaAsync(ct);
    }

    private async Task EnsureTunnelAsync(CancellationToken ct)
    {
        if (await TunnelUpAsync(ct)) return;
        if (_tunnel is not { HasExited: false })
        {
            var pairDir = Path.Combine(_tools, "tunnel-pairrecords");
            Directory.CreateDirectory(pairDir);
            // --userspace: no admin rights or wintun driver needed.
            _tunnel = StartBackground(Ios, ["tunnel", "start", "--userspace", $"--pair-record-path={pairDir}"], "tunnel.log");
        }
        for (var i = 0; i < 30; i++)
        {
            if (await TunnelUpAsync(ct)) return;
            await Task.Delay(1000, ct);
        }
        throw new IOException("The tunnel to the iPhone didn't come up. If the phone asks to trust this computer, tap Trust.");
    }

    private async Task<bool> TunnelUpAsync(CancellationToken ct)
    {
        var (_, output) = await RunAsync(Ios, ["tunnel", "ls"], TimeSpan.FromSeconds(10), ct);
        return output.Contains("\"address\"", StringComparison.Ordinal);
    }

    private async Task EnsureImageAsync(CancellationToken ct)
    {
        var imageDir = Path.Combine(_tools, "devimages");
        Directory.CreateDirectory(imageDir);
        var (code, _) = await RunAsync(Ios, ["image", "auto", $"--basedir={imageDir}"], TimeSpan.FromMinutes(3), ct);
        if (code == 0) return;

        // go-ios can't verify gs.apple.com on Windows (Apple's root CA isn't in the Windows store);
        // pymobiledevice3 requests the same signature over plain HTTP. The phone checks Apple's signature itself.
        var (pyCode, _) = await RunAsync("py", ["-m", "pymobiledevice3", "mounter", "auto-mount"], TimeSpan.FromMinutes(3), ct);
        if (pyCode != 0)
            throw new IOException("Couldn't mount the developer image. Install the fallback with: py -m pip install pymobiledevice3");
    }

    private async Task StartWdaAsync(CancellationToken ct)
    {
        if (_runwda is { HasExited: false }) return;
        var id = settings.WdaBundleId ?? await FindWdaBundleIdAsync(ct);
        settings.WdaBundleId = id;
        _runwda = StartBackground(Ios,
            ["runwda", $"--bundleid={id}", $"--testrunnerbundleid={id}", "--xctestconfig=WebDriverAgentRunner.xctest"],
            "runwda.log");
    }

    private async Task<string> FindWdaBundleIdAsync(CancellationToken ct)
    {
        var (_, output) = await RunAsync(Ios, ["apps"], TimeSpan.FromSeconds(30), ct);
        var json = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('['));
        if (json != null)
        {
            foreach (var app in JsonDocument.Parse(json).RootElement.EnumerateArray())
            {
                var id = app.TryGetProperty("CFBundleIdentifier", out var b) ? b.GetString() : null;
                if (id?.Contains("WebDriverAgent", StringComparison.OrdinalIgnoreCase) == true) return id;
            }
        }
        throw new InvalidOperationException("WebDriverAgent isn't installed on the iPhone. Sideload tools\\WDA.ipa with Sideloadly.");
    }

    /// <summary>Forget the cached bundle id, e.g. after re-signing WDA changed it.</summary>
    public void ResetBundleId() => settings.WdaBundleId = null;

    public bool WdaProcessExited => _runwda is { HasExited: true };

    private Process StartBackground(string file, string[] args, string logName)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var log = new StreamWriter(Path.Combine(LogDir, logName), append: false) { AutoFlush = true };
        _logs.Add(log);
        var p = new Process { StartInfo = psi };
        // Output callbacks can still arrive after the process exits, so the writer lives until Dispose().
        void Write(string? line)
        {
            if (line == null) return;
            lock (log)
            {
                try { log.WriteLine(line); }
                catch (ObjectDisposedException) { } // late output during shutdown
            }
        }
        p.OutputDataReceived += (_, e) => Write(e.Data);
        p.ErrorDataReceived += (_, e) => Write(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, await stdout);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return (-1, "");
        }
        finally
        {
            _ = stderr.ContinueWith(_ => { }, TaskScheduler.Default); // observe
        }
    }

    public void Dispose()
    {
        foreach (var p in new[] { _runwda, _tunnel })
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            p?.Dispose();
        }
        foreach (var log in _logs) lock (log) log.Dispose();
    }
}
