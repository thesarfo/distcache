using System.Security.Cryptography.X509Certificates;

namespace DistCache.Core.Configuration;

/// <summary>
/// TLS/mTLS configuration for inter-node gRPC communication.
/// Supports both file-based certificates (production) and in-memory certificates (tests).
/// Use <c>DistCache.Admin.Extensions.TlsOptionsExtensions.FromConfiguration</c> to bind from
/// <c>IConfiguration</c> in an ASP.NET Core host.
/// </summary>
public sealed record TlsOptions
{
    // ── File-based (production) ────────────────────────────────────────────

    /// <summary>Gets the path to the node's PFX certificate file.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>Gets the passphrase for the PFX file at <see cref="CertificatePath"/>, if encrypted.</summary>
    public string? CertificatePassword { get; init; }

    /// <summary>Gets the path to the trusted CA certificate file (PEM or DER) used to validate peer certificates.</summary>
    public string? TrustedCaCertificatePath { get; init; }

    // ── In-memory (tests / programmatic) ─────────────────────────────────

    /// <summary>Gets the node certificate used for both server TLS and client authentication.</summary>
    public X509Certificate2? NodeCertificate { get; init; }

    /// <summary>Gets the CA certificate used to validate peer certificates.</summary>
    public X509Certificate2? TrustedCaCertificate { get; init; }

    // ── Dev mode ──────────────────────────────────────────────────────────

    /// <summary>
    /// Gets a value indicating whether mTLS validation is relaxed.
    /// When <see langword="true"/>: the server does not require a client certificate and the client
    /// skips server certificate validation. Use only in local development environments.
    /// </summary>
    public bool AllowInsecure { get; init; }

    // ── Internal resolution helpers ───────────────────────────────────────

    /// <summary>
    /// Returns the resolved node certificate: prefers <see cref="NodeCertificate"/> (in-memory),
    /// falls back to loading from <see cref="CertificatePath"/> when set.
    /// </summary>
    /// <returns>The node certificate, or <see langword="null"/> if neither source is configured.</returns>
    public X509Certificate2? ResolveNodeCertificate()
    {
        if (NodeCertificate is not null)
        {
            return NodeCertificate;
        }

        if (!string.IsNullOrWhiteSpace(CertificatePath))
        {
            return new X509Certificate2(CertificatePath, CertificatePassword);
        }

        return null;
    }

    /// <summary>
    /// Returns the resolved trusted CA certificate: prefers <see cref="TrustedCaCertificate"/> (in-memory),
    /// falls back to loading from <see cref="TrustedCaCertificatePath"/> when set.
    /// </summary>
    /// <returns>The CA certificate, or <see langword="null"/> if neither source is configured.</returns>
    public X509Certificate2? ResolveTrustedCa()
    {
        if (TrustedCaCertificate is not null)
        {
            return TrustedCaCertificate;
        }

        if (!string.IsNullOrWhiteSpace(TrustedCaCertificatePath))
        {
            return new X509Certificate2(TrustedCaCertificatePath);
        }

        return null;
    }
}
