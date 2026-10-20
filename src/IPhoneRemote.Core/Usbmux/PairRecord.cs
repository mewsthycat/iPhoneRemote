using System.Security.Cryptography.X509Certificates;
using System.Text;
using Claunia.PropertyList;

namespace IPhoneRemote.Core.Usbmux;

/// <summary>
/// Credentials created when the user taps "Trust This Computer". They let the host open a
/// TLS lockdown session and start developer services.
/// </summary>
public sealed record PairRecord(string HostId, string SystemBuid, string HostCertificatePem, string HostPrivateKeyPem)
{
    internal static PairRecord FromPlist(NSDictionary d) => new(
        ((NSString)d["HostID"]).Content,
        ((NSString)d["SystemBUID"]).Content,
        Encoding.ASCII.GetString(((NSData)d["HostCertificate"]).Bytes),
        Encoding.ASCII.GetString(((NSData)d["HostPrivateKey"]).Bytes));

    public X509Certificate2 CreateClientCertificate()
    {
        using var ephemeral = X509Certificate2.CreateFromPem(HostCertificatePem, HostPrivateKeyPem);
        // SChannel can't use ephemeral keys for client auth; round-trip through PFX to persist the key.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
