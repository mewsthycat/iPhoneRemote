using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Claunia.PropertyList;
using IPhoneRemote.Core.Usbmux;

namespace IPhoneRemote.Core.Lockdown;

/// <summary>
/// Client for lockdownd (device port 62078), the iOS service broker.
/// Framing: 4-byte big-endian length + XML plist. Basic GetValue works without a session;
/// StartService for most services requires StartSession (TLS using the pair record).
/// </summary>
public sealed class LockdownClient : IDisposable
{
    public const int Port = 62078;
    private const string Label = "iPhoneRemote";
    private const int MaxMessageSize = 16 * 1024 * 1024;

    private Stream _stream;
    private string? _sessionId;

    private LockdownClient(Stream stream) => _stream = stream;

    public static async Task<LockdownClient> ConnectAsync(UsbmuxDevice device, CancellationToken ct = default)
    {
        var mux = await UsbmuxClient.ConnectAsync(ct: ct);
        Stream stream;
        try { stream = await mux.ConnectToDeviceAsync(device.DeviceId, Port, ct); }
        catch { mux.Dispose(); throw; }

        var client = new LockdownClient(stream);
        try
        {
            var type = await client.RequestAsync(new NSDictionary { { "Request", "QueryType" } }, ct);
            if ((type.TryGetValue("Type", out var t) ? t.ToString() : null) != "com.apple.mobile.lockdown")
                throw new IOException("Unexpected lockdown QueryType response");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Read a lockdown value. Pass null key to get the whole (domain) dictionary.</summary>
    public async Task<NSObject?> GetValueAsync(string? key, string? domain = null, CancellationToken ct = default)
    {
        var req = new NSDictionary { { "Request", "GetValue" } };
        if (key != null) req.Add("Key", key);
        if (domain != null) req.Add("Domain", domain);
        var reply = await RequestAsync(req, ct);
        return reply.TryGetValue("Value", out var v) ? v : null;
    }

    /// <summary>Upgrade the connection to TLS using the host's pair record.</summary>
    public async Task StartSessionAsync(PairRecord pair, CancellationToken ct = default)
    {
        var reply = await RequestAsync(new NSDictionary
        {
            { "Request", "StartSession" },
            { "HostID", pair.HostId },
            { "SystemBUID", pair.SystemBuid },
        }, ct);
        _sessionId = ((NSString)reply["SessionID"]).Content;

        if (reply.TryGetValue("EnableSessionSSL", out var ssl) && ssl is NSNumber b && b.ToBool())
        {
            var tls = new SslStream(_stream, leaveInnerStreamOpen: false,
                // The device presents a self-signed cert minted at pairing time.
                userCertificateValidationCallback: (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "iPhone",
                ClientCertificates = [pair.CreateClientCertificate()],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
            _stream = tls;
        }
    }

    /// <summary>Ask lockdownd to launch a service; returns the device port to connect to via usbmux.</summary>
    public async Task<(int Port, bool EnableServiceSsl)> StartServiceAsync(string service, CancellationToken ct = default)
    {
        if (_sessionId == null) throw new InvalidOperationException("Call StartSessionAsync first.");
        var reply = await RequestAsync(new NSDictionary { { "Request", "StartService" }, { "Service", service } }, ct);
        var ssl = reply.TryGetValue("EnableServiceSSL", out var s) && s is NSNumber n && n.ToBool();
        return (((NSNumber)reply["Port"]).ToInt(), ssl);
    }

    private async Task<NSDictionary> RequestAsync(NSDictionary request, CancellationToken ct)
    {
        request.Add("Label", Label);
        var body = Encoding.UTF8.GetBytes(request.ToXmlPropertyList());
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await _stream.WriteAsync(header, ct);
        await _stream.WriteAsync(body, ct);
        await _stream.FlushAsync(ct);

        await _stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxMessageSize)
            throw new IOException($"lockdown sent a malformed message (length {length})");
        var reply = new byte[length];
        await _stream.ReadExactlyAsync(reply, ct);
        var dict = (NSDictionary)PropertyListParser.Parse(reply);
        if (dict.TryGetValue("Error", out var err))
            throw new IOException($"lockdown {((NSString)request["Request"]).Content} failed: {err}");
        return dict;
    }

    public void Dispose() => _stream.Dispose();
}
