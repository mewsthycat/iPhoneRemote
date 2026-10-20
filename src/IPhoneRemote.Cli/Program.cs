using System.Diagnostics;
using IPhoneRemote.Core.Lockdown;
using IPhoneRemote.Core.Usbmux;
using IPhoneRemote.Core.Wda;

// Test harness for the USB stack. Usage:
//   iremote list                 - devices visible to Apple Mobile Device Service
//   iremote watch                - print attach/detach events
//   iremote info                 - lockdown values (no pairing needed for basics)
//   iremote session              - TLS lockdown session using the pair record (proves trust)
//   iremote forward              - expose WDA 8100/9100 on localhost
//   iremote wda                  - forward + WDA status + window size + MJPEG fps probe
//   iremote tap <x> <y>          - forward + tap at (x, y) in points
var cmd = args.Length > 0 ? args[0] : "list";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

try
{
    switch (cmd)
    {
        case "list":
            await ListAsync();
            break;
        case "watch":
            using (var mux = await UsbmuxClient.ConnectAsync(ct: ct))
            {
                Console.WriteLine("Listening for devices (Ctrl+C to stop)...");
                await foreach (var ev in mux.ListenAsync(ct))
                    Console.WriteLine($"{ev.Type,-9} id={ev.Device.DeviceId} udid={ev.Device.Udid} via {ev.Device.ConnectionType}");
            }
            break;
        case "info":
            await InfoAsync(await FirstUsbDeviceAsync());
            break;
        case "session":
            await SessionAsync(await FirstUsbDeviceAsync());
            break;
        case "forward":
            await using (await StartWdaForwardsAsync(await FirstUsbDeviceAsync()))
            {
                Console.WriteLine("Forwarding 127.0.0.1:8100 -> WDA HTTP, 127.0.0.1:9100 -> WDA MJPEG. Ctrl+C to stop.");
                await Task.Delay(Timeout.Infinite, ct);
            }
            break;
        case "wda":
            await using (await StartWdaForwardsAsync(await FirstUsbDeviceAsync()))
                await WdaProbeAsync();
            break;
        case "tap" when args.Length == 3:
            await using (await StartWdaForwardsAsync(await FirstUsbDeviceAsync()))
            {
                using var wda = new WdaClient(new Uri("http://127.0.0.1:8100"));
                await wda.CreateSessionAsync(ct);
                var sw = Stopwatch.StartNew();
                await wda.TapAsync(double.Parse(args[1]), double.Parse(args[2]), ct);
                Console.WriteLine($"Tapped in {sw.ElapsedMilliseconds} ms");
            }
            break;
        default:
            Console.WriteLine("Commands: list | watch | info | session | forward | wda | tap <x> <y>");
            return 1;
    }
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

async Task ListAsync()
{
    using var mux = await UsbmuxClient.ConnectAsync(ct: ct);
    var devices = await mux.ListDevicesAsync(ct);
    Console.WriteLine($"usbmux reachable; {devices.Count} device(s):");
    foreach (var d in devices)
        Console.WriteLine($"  id={d.DeviceId} udid={d.Udid} via {d.ConnectionType} pid=0x{d.ProductId:X4}");
}

async Task<UsbmuxDevice> FirstUsbDeviceAsync()
{
    using var mux = await UsbmuxClient.ConnectAsync(ct: ct);
    var devices = await mux.ListDevicesAsync(ct);
    return devices.FirstOrDefault(d => d.ConnectionType == "USB")
           ?? throw new InvalidOperationException("No iPhone connected over USB. Plug it in and unlock it.");
}

async Task InfoAsync(UsbmuxDevice device)
{
    using var lockdown = await LockdownClient.ConnectAsync(device, ct);
    foreach (var key in new[] { "DeviceName", "ProductType", "ProductVersion", "BuildVersion", "UniqueDeviceID", "HardwareModel" })
    {
        try { Console.WriteLine($"  {key,-15} {await lockdown.GetValueAsync(key, ct: ct)}"); }
        catch (IOException ex) { Console.WriteLine($"  {key,-15} <{ex.Message}>"); }
    }
}

async Task SessionAsync(UsbmuxDevice device)
{
    PairRecord pair;
    using (var mux = await UsbmuxClient.ConnectAsync(ct: ct))
        pair = await mux.ReadPairRecordAsync(device.Udid, ct);
    using var lockdown = await LockdownClient.ConnectAsync(device, ct);
    await lockdown.StartSessionAsync(pair, ct);
    Console.WriteLine("TLS lockdown session established (device trusts this PC).");
    Console.WriteLine($"  DeviceName      {await lockdown.GetValueAsync("DeviceName", ct: ct)}");
    Console.WriteLine($"  WiFiAddress     {await lockdown.GetValueAsync("WiFiAddress", ct: ct)}");
    Console.WriteLine($"  BluetoothAddr   {await lockdown.GetValueAsync("BluetoothAddress", ct: ct)}");
}

async Task<AsyncDisposableGroup> StartWdaForwardsAsync(UsbmuxDevice device)
{
    var http = new PortForwarder(device, 8100, 8100);
    var mjpeg = new PortForwarder(device, 9100, 9100);
    http.Start();
    mjpeg.Start();
    await Task.Yield();
    return new AsyncDisposableGroup(http, mjpeg);
}

async Task WdaProbeAsync()
{
    using var wda = new WdaClient(new Uri("http://127.0.0.1:8100"));
    try
    {
        Console.WriteLine($"WDA status: {(await wda.StatusAsync(ct))?.ToJsonString()}");
    }
    catch (HttpRequestException)
    {
        Console.Error.WriteLine("WDA not reachable on device port 8100. Launch it first, e.g. `ios runwda` (go-ios) " +
                                "or `pymobiledevice3 developer dvt xcuitest <WDA bundle id>`.");
        throw;
    }
    await wda.CreateSessionAsync(ct);
    var (w, h) = await wda.GetWindowSizeAsync(ct);
    Console.WriteLine($"Screen: {w} x {h} points");
    await wda.ConfigureStreamAsync(framerate: 30, scalingFactor: 50, quality: 60, ct: ct);

    Console.WriteLine("Measuring MJPEG stream for 5 s...");
    using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
    window.CancelAfter(TimeSpan.FromSeconds(5));
    int frames = 0;
    long bytes = 0;
    try
    {
        await foreach (var jpeg in MjpegReader.ReadFramesAsync(new Uri("http://127.0.0.1:9100"), window.Token))
        {
            frames++;
            bytes += jpeg.Length;
            if (frames == 1) await File.WriteAllBytesAsync("first-frame.jpg", jpeg, ct);
        }
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    Console.WriteLine($"  {frames / 5.0:F1} fps, avg {(frames > 0 ? bytes / frames / 1024 : 0)} KB/frame (first frame saved to first-frame.jpg)");
}

sealed class AsyncDisposableGroup(params IAsyncDisposable[] items) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        foreach (var item in items) await item.DisposeAsync();
    }
}
