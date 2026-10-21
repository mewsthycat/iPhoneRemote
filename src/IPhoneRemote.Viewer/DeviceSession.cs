using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using IPhoneRemote.Core.Lockdown;
using IPhoneRemote.Core.Usbmux;
using IPhoneRemote.Core.Wda;

namespace IPhoneRemote.Viewer;

/// <summary>
/// One connected iPhone: USB port forwards to WDA, a WDA session, the MJPEG stream, and a
/// single ordered input queue so taps/typing never overtake each other.
/// </summary>
public sealed class DeviceSession : IAsyncDisposable
{
    private const int WdaPort = 8100;
    private const int MjpegPort = 9100;

    private readonly PortForwarder _http;
    private readonly PortForwarder _mjpeg;
    private readonly WdaClient _wda;
    private readonly Channel<(string Label, Func<WdaClient, Task> Action)> _input =
        Channel.CreateUnbounded<(string, Func<WdaClient, Task>)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _inputLoop;

    public string DeviceName { get; }
    public double PointsWidth { get; }
    public double PointsHeight { get; }

    /// <summary>Raised on a thread-pool thread after each input action: (label, elapsed ms, error or null).</summary>
    public event Action<string, long, Exception?>? InputCompleted;

    private DeviceSession(string name, PortForwarder http, PortForwarder mjpeg, WdaClient wda, double w, double h)
    {
        DeviceName = name;
        _http = http;
        _mjpeg = mjpeg;
        _wda = wda;
        PointsWidth = w;
        PointsHeight = h;
        _inputLoop = Task.Run(RunInputLoopAsync);
    }

    public static async Task<DeviceSession> ConnectAsync(
        WdaLauncher launcher, StreamProfile stream, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Looking for your iPhone…");
        var device = await WaitForDeviceAsync(progress, ct);
        var name = await TryGetDeviceNameAsync(device, ct) ?? "iPhone";

        var http = new PortForwarder(device, WdaPort, WdaPort);
        var mjpeg = new PortForwarder(device, MjpegPort, MjpegPort);
        http.Start();
        mjpeg.Start();
        var wda = new WdaClient(new Uri($"http://127.0.0.1:{WdaPort}"));
        try
        {
            if (!await IsReadyAsync(wda, ct))
            {
                await launcher.StartAsync(progress, ct);
                progress.Report("Waiting for WebDriverAgent…");
                var sw = Stopwatch.StartNew();
                while (!await IsReadyAsync(wda, ct))
                {
                    if (launcher.WdaProcessExited || sw.Elapsed > TimeSpan.FromSeconds(90))
                    {
                        launcher.ResetBundleId();
                        throw new IOException(
                            "WebDriverAgent didn't start. If it's been 7 days, re-sign tools\\WDA.ipa in Sideloadly. " +
                            $"Details: {launcher.RunWdaLogPath}");
                    }
                    await Task.Delay(1000, ct);
                }
            }

            progress.Report("Starting session…");
            await wda.CreateSessionAsync(ct);
            var (w, h) = await wda.GetWindowSizeAsync(ct);
            await wda.ConfigureStreamAsync(stream.Framerate, stream.ScalingFactor, stream.JpegQuality, ct);
            return new DeviceSession(name, http, mjpeg, wda, w, h);
        }
        catch
        {
            wda.Dispose();
            await http.DisposeAsync();
            await mjpeg.DisposeAsync();
            throw;
        }
    }

    private static async Task<UsbmuxDevice> WaitForDeviceAsync(IProgress<string> progress, CancellationToken ct)
    {
        var told = false;
        while (true)
        {
            using (var mux = await UsbmuxClient.ConnectAsync(ct: ct))
            {
                var device = (await mux.ListDevicesAsync(ct)).FirstOrDefault(d => d.ConnectionType == "USB");
                if (device != null) return device;
            }
            if (!told)
            {
                progress.Report("Plug in your iPhone and unlock it.");
                told = true;
            }
            await Task.Delay(1000, ct);
        }
    }

    private static async Task<string?> TryGetDeviceNameAsync(UsbmuxDevice device, CancellationToken ct)
    {
        try
        {
            using var lockdown = await LockdownClient.ConnectAsync(device, ct);
            return (await lockdown.GetValueAsync("DeviceName", ct: ct))?.ToString();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return null; }
    }

    private static async Task<bool> IsReadyAsync(WdaClient wda, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return (await wda.StatusAsync(cts.Token))?["ready"]?.GetValue<bool>() ?? false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>JPEG frames; each is only valid until the next one is requested (see MjpegReader).</summary>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> FramesAsync(CancellationToken ct) =>
        MjpegReader.ReadFramesAsync(new Uri($"http://127.0.0.1:{MjpegPort}"), ct);

    public void Enqueue(string label, Func<WdaClient, Task> action) => _input.Writer.TryWrite((label, action));

    private async Task RunInputLoopAsync()
    {
        try
        {
            await foreach (var (label, action) in _input.Reader.ReadAllAsync(_cts.Token))
            {
                var sw = Stopwatch.StartNew();
                Exception? error = null;
                try
                {
                    try { await action(_wda); }
                    catch (IOException ex) when (ex.Message.Contains("invalid session id", StringComparison.OrdinalIgnoreCase))
                    {
                        // WDA drops sessions when it restarts; recreate once and retry.
                        await _wda.CreateSessionAsync(_cts.Token);
                        await action(_wda);
                    }
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    // HttpClient reports its timeout as a cancellation. It must not end the loop:
                    // that silently dropped every later tap and button press.
                    error = new TimeoutException(
                        $"the phone didn't respond within {WdaClient.RequestTimeout.TotalSeconds:0} s");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex;
                }
                if (_cts.IsCancellationRequested) break; // session closing: don't report its aborted input
                InputCompleted?.Invoke(label, sw.ElapsedMilliseconds, error);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _input.Writer.TryComplete();
        _cts.Cancel();
        // Disposing the client aborts an in-flight request; otherwise a slow phone would
        // hold up Reconnect (F5) for up to the 30 s request timeout.
        _wda.Dispose();
        await _inputLoop;
        await _http.DisposeAsync();
        await _mjpeg.DisposeAsync();
        _cts.Dispose();
    }
}
