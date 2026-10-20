using System.Net;
using System.Net.Sockets;

namespace IPhoneRemote.Core.Usbmux;

/// <summary>
/// Exposes a TCP port on the device as 127.0.0.1:localPort (like `iproxy` / `ios forward`).
/// Used to reach WebDriverAgent's HTTP API (8100) and MJPEG stream (9100) over USB.
/// </summary>
public sealed class PortForwarder(UsbmuxDevice device, int localPort, int devicePort) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, localPort);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int LocalPort => localPort;
    public int DevicePort => devicePort;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient local;
            try { local = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            _ = PipeAsync(local, ct);
        }
    }

    private async Task PipeAsync(TcpClient local, CancellationToken ct)
    {
        local.NoDelay = true;
        using (local)
        {
            try
            {
                var mux = await UsbmuxClient.ConnectAsync(ct: ct);
                await using var remote = await mux.ConnectToDeviceAsync(device.DeviceId, devicePort, ct);
                var localStream = local.GetStream();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var up = localStream.CopyToAsync(remote, linked.Token);
                var down = remote.CopyToAsync(localStream, linked.Token);
                await Task.WhenAny(up, down);
                linked.Cancel();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                Console.Error.WriteLine($"[forward {localPort}->{devicePort}] {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop != null) await _acceptLoop;
        _cts.Dispose();
    }
}
