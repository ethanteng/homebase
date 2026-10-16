using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Homebase.Core.Sync;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Homebase.Tests;

/// <summary>
/// A computer's Syncthing certificate and key, as the files in its Syncthing directory hold them:
/// what it signs a release with, and what its device ID is the hash of.
/// </summary>
public sealed record SyncthingIdentity(string Certificate, string Key)
{
    public string DeviceId => ComputerReleases.DeviceIdOf(Certificate)!;

    /// <summary>
    /// A certificate Syncthing 2.0.12 made with <c>syncthing generate</c>, and the device ID it printed
    /// for it: the check that IDs are worked out as Syncthing does. Its key isn't kept here, nor needed:
    /// <see cref="Ed25519"/> writes one in the same form.
    /// </summary>
    public const string FromSyncthingCertificate =
        """
        -----BEGIN CERTIFICATE-----
        MIIBoDCCAVKgAwIBAgIJAPQHxkacHKJWMAUGAytlcDBKMRIwEAYDVQQKEwlTeW5j
        dGhpbmcxIDAeBgNVBAsTF0F1dG9tYXRpY2FsbHkgR2VuZXJhdGVkMRIwEAYDVQQD
        EwlzeW5jdGhpbmcwHhcNMjYxMDE2MDAwMDAwWhcNNDYxMDExMDAwMDAwWjBKMRIw
        EAYDVQQKEwlTeW5jdGhpbmcxIDAeBgNVBAsTF0F1dG9tYXRpY2FsbHkgR2VuZXJh
        dGVkMRIwEAYDVQQDEwlzeW5jdGhpbmcwKjAFBgMrZXADIQD48eEcrpKMhzYeTTDB
        iBcnEmAKJxa4fKozldy8ABpGy6NVMFMwDgYDVR0PAQH/BAQDAgWgMB0GA1UdJQQW
        MBQGCCsGAQUFBwMBBggrBgEFBQcDAjAMBgNVHRMBAf8EAjAAMBQGA1UdEQQNMAuC
        CXN5bmN0aGluZzAFBgMrZXADQQALkL3iIDhMBy6+hV62pOzAf4fJPSaIkI+XkYtM
        w+kpYEJpoqHkioXiXMWGA/j8JH5ac4tgw3qWLtpgGMgpa1UN
        -----END CERTIFICATE-----
        """;

    public const string FromSyncthingDeviceId = "KGKMJFL-VXUIF4R-TT5TUEZ-2IXD5MX-5MOU63G-M3DDTIO-P27TPWT-TZ7KEQG";

    /// <summary>What Syncthing 2 makes: Ed25519, its key as PKCS #8.</summary>
    public static SyncthingIdentity Ed25519()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var certificate = new X509V3CertificateGenerator();
        certificate.SetSerialNumber(Org.BouncyCastle.Math.BigInteger.ValueOf(RandomNumberGenerator.GetInt32(int.MaxValue)));
        certificate.SetIssuerDN(new X509Name("CN=syncthing"));
        certificate.SetSubjectDN(new X509Name("CN=syncthing"));
        certificate.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certificate.SetNotAfter(DateTime.UtcNow.AddYears(20));
        certificate.SetPublicKey(pair.Public);
        return new SyncthingIdentity(
            PemEncoding.WriteString("CERTIFICATE", certificate.Generate(new Asn1SignatureFactory("Ed25519", pair.Private)).GetEncoded()),
            PemEncoding.WriteString("PRIVATE KEY", PrivateKeyInfoFactory.CreatePrivateKeyInfo(pair.Private).GetDerEncoded()));
    }

    /// <summary>What Syncthing 1 made: ECDSA on P-384, its key in the older EC PRIVATE KEY form.</summary>
    public static SyncthingIdentity Ecdsa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var certificate = new CertificateRequest("CN=syncthing", key, HashAlgorithmName.SHA384)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return new SyncthingIdentity(certificate.ExportCertificatePem(), key.ExportECPrivateKeyPem());
    }

    /// <summary>What the first Syncthings made: RSA, its key in the RSA PRIVATE KEY form.</summary>
    public static SyncthingIdentity Rsa(int bits = 3072)
    {
        using var key = RSA.Create(bits);
        using var certificate = new CertificateRequest("CN=syncthing", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return new SyncthingIdentity(certificate.ExportCertificatePem(), key.ExportRSAPrivateKeyPem());
    }

    /// <summary>Puts this where the Uncloud app's Syncthing keeps its own.</summary>
    public void WriteTo(string syncthingDirectory)
    {
        Directory.CreateDirectory(syncthingDirectory);
        File.WriteAllText(Path.Combine(syncthingDirectory, "cert.pem"), Certificate);
        File.WriteAllText(Path.Combine(syncthingDirectory, "key.pem"), Key);
    }
}
