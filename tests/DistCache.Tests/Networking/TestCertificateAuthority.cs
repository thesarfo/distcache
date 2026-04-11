using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DistCache.Tests.Networking;

/// <summary>
/// Generates ephemeral in-memory X.509 certificates for integration tests.
/// All certificates are ECDSA P-256, valid for 365 days, and never touch the OS certificate store.
/// </summary>
internal static class TestCertificateAuthority
{
    /// <summary>Creates a self-signed CA certificate suitable for signing node certificates.</summary>
    /// <returns>An exportable CA certificate with its private key.</returns>
    internal static X509Certificate2 CreateCa()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=DistCache Test CA", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        return RoundTrip(cert);
    }

    /// <summary>
    /// Issues a node certificate signed by <paramref name="ca"/> for use as both a TLS server
    /// certificate and a client certificate (EKU includes id-kp-serverAuth and id-kp-clientAuth).
    /// </summary>
    /// <param name="ca">The CA certificate (with private key) that will sign the new certificate.</param>
    /// <param name="commonName">Common name to embed in the certificate subject.</param>
    /// <returns>An exportable leaf certificate with its private key.</returns>
    internal static X509Certificate2 IssueNode(X509Certificate2 ca, string commonName)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

        // Both serverAuth (1.3.6.1.5.5.7.3.1) and clientAuth (1.3.6.1.5.5.7.3.2) so the
        // same cert can be used on both ends of the mTLS handshake.
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")],
            critical: false));

        byte[] serial = new byte[16];
        RandomNumberGenerator.Fill(serial);

        // Node cert validity is 2 days shorter than the CA so notAfter < ca.NotAfter.
        X509Certificate2 signed = req.Create(
            ca,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(363),
            serial);

        // CopyWithPrivateKey returns a cert without a key store, RoundTrip fixes that.
        return RoundTrip(signed.CopyWithPrivateKey(key));
    }

    // PFX round-trip without EphemeralKeySet so that Windows SChannel can access the
    // private key via CNG. EphemeralKeySet keys are not reachable from kernel-mode
    // SChannel on some Windows configurations, causing Kestrel TLS handshakes to fail.
    private static X509Certificate2 RoundTrip(X509Certificate2 cert)
    {
        byte[] pfx = cert.Export(X509ContentType.Pfx, "test");
        return new X509Certificate2(pfx, "test", X509KeyStorageFlags.Exportable);
    }
}
