using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Claunia.PropertyList;

namespace IPhoneRemote.Core.Usbmux;

/// <summary>
/// Client for the usbmux protocol. On Windows the daemon is "Apple Mobile Device Service"
/// (installed by iTunes or the Apple Devices app) and listens on TCP 127.0.0.1:27015.
/// On macOS/Linux it is usbmuxd on /var/run/usbmuxd. The wire protocol is identical:
///   16-byte little-endian header { length, version=1, type=8 (plist), tag } + XML plist payload.
/// </summary>
public sealed class UsbmuxClient : IDisposable
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 27015;

    private const int ProtocolVersionPlist = 1;
    private const int MessageTypePlist = 8;
    private const int HeaderSize = 16;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private int _tag;

    private UsbmuxClient(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
    }

    public static async Task<UsbmuxClient> ConnectAsync(string host = DefaultHost, int port = DefaultPort, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new InvalidOperationException(
                $"Cannot reach usbmux at {host}:{port}. Install iTunes or the 'Apple Devices' app " +
                "and make sure 'Apple Mobile Device Service' is running.", ex);
        }
        return new UsbmuxClient(tcp);
    }

    public async Task<IReadOnlyList<UsbmuxDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(NewRequest("ListDevices"), ct);
        var list = (NSArray)reply["DeviceList"];
        return list.Select(o => UsbmuxDevice.FromPlist((NSDictionary)o)).ToList();
    }

    /// <summary>Returns the pair record (host cert/key, root cert, HostID, SystemBUID) for a device.</summary>
    public async Task<PairRecord> ReadPairRecordAsync(string udid, CancellationToken ct = default)
    {
        var req = NewRequest("ReadPairRecord");
        req.Add("PairRecordID", udid);
        var reply = await RequestAsync(req, ct);
        if (reply.TryGetValue("PairRecordData", out var data) && data is NSData bytes)
            return PairRecord.FromPlist((NSDictionary)PropertyListParser.Parse(bytes.Bytes));

        // Older Apple Mobile Device Service builds don't implement ReadPairRecord; fall back to disk.
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                "Apple", "Lockdown", udid + ".plist");
        if (File.Exists(path))
            return PairRecord.FromPlist((NSDictionary)PropertyListParser.Parse(path));

        throw new InvalidOperationException(
            $"No pair record for {udid}. Unlock the iPhone, connect it, and tap 'Trust This Computer'.");
    }

    /// <summary>
    /// Subscribe to attach/detach events. This connection is dedicated to events afterwards.
    /// </summary>
    public async IAsyncEnumerable<UsbmuxEvent> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var reply = await RequestAsync(NewRequest("Listen"), ct);
        EnsureResultOk(reply, "Listen");
        while (!ct.IsCancellationRequested)
        {
            var (_, msg) = await ReadPacketAsync(ct);
            var type = ((NSString)msg["MessageType"]).Content;
            switch (type)
            {
                case "Attached":
                    yield return new UsbmuxEvent(UsbmuxEventType.Attached, UsbmuxDevice.FromPlist(msg));
                    break;
                case "Detached":
                    yield return new UsbmuxEvent(UsbmuxEventType.Detached,
                        new UsbmuxDevice(((NSNumber)msg["DeviceID"]).ToInt(), "", "", 0));
                    break;
            }
        }
    }

    /// <summary>
    /// Ask usbmux to open a TCP connection to <paramref name="devicePort"/> on the device.
    /// On success, this client's stream becomes a raw byte pipe to that port and must not be
    /// used for further usbmux requests. Ownership of the stream passes to the caller.
    /// </summary>
    public async Task<Stream> ConnectToDeviceAsync(int deviceId, int devicePort, CancellationToken ct = default)
    {
        var req = NewRequest("Connect");
        req.Add("DeviceID", deviceId);
        // usbmux expects the port in network byte order packed into an integer.
        req.Add("PortNumber", (int)BinaryPrimitives.ReverseEndianness((ushort)devicePort));
        var reply = await RequestAsync(req, ct);
        EnsureResultOk(reply, $"Connect to device port {devicePort}");
        return _stream;
    }

    private static NSDictionary NewRequest(string messageType) => new()
    {
        { "MessageType", messageType },
        { "ClientVersionString", "iPhoneRemote-0.1" },
        { "ProgName", "iPhoneRemote" },
        { "kLibUSBMuxVersion", 3 },
    };

    private async Task<NSDictionary> RequestAsync(NSDictionary request, CancellationToken ct)
    {
        var tag = ++_tag;
        await WritePacketAsync(request, tag, ct);
        while (true)
        {
            var (replyTag, reply) = await ReadPacketAsync(ct);
            if (replyTag == tag) return reply;
        }
    }

    private async Task WritePacketAsync(NSDictionary payload, int tag, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToXmlPropertyList());
        var packet = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0), packet.Length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), ProtocolVersionPlist);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), MessageTypePlist);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), tag);
        body.CopyTo(packet, HeaderSize);
        await _stream.WriteAsync(packet, ct);
    }

    private async Task<(int Tag, NSDictionary Payload)> ReadPacketAsync(CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await _stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0));
        var tag = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var body = new byte[length - HeaderSize];
        await _stream.ReadExactlyAsync(body, ct);
        return (tag, (NSDictionary)PropertyListParser.Parse(body));
    }

    private static void EnsureResultOk(NSDictionary reply, string what)
    {
        if (reply.TryGetValue("Number", out var n) && n is NSNumber num && num.ToInt() != 0)
        {
            var reason = num.ToInt() switch
            {
                2 => "device not connected",
                3 => "connection refused (nothing listening on that device port)",
                6 => "bad version",
                _ => $"error {num.ToInt()}",
            };
            throw new IOException($"usbmux {what} failed: {reason}");
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}

public sealed record UsbmuxDevice(int DeviceId, string Udid, string ConnectionType, int ProductId)
{
    internal static UsbmuxDevice FromPlist(NSDictionary entry)
    {
        var props = (NSDictionary)entry["Properties"];
        return new UsbmuxDevice(
            ((NSNumber)props["DeviceID"]).ToInt(),
            props.TryGetValue("SerialNumber", out var sn) ? ((NSString)sn).Content : "",
            props.TryGetValue("ConnectionType", out var c) ? ((NSString)c).Content : "",
            props.TryGetValue("ProductID", out var p) ? ((NSNumber)p).ToInt() : 0);
    }
}

public enum UsbmuxEventType { Attached, Detached }

public sealed record UsbmuxEvent(UsbmuxEventType Type, UsbmuxDevice Device);
