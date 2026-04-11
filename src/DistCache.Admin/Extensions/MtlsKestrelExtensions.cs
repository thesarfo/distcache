using System.Security.Cryptography.X509Certificates;
using DistCache.Core.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace DistCache.Admin.Extensions;

/// <summary>
/// Extension methods for configuring Kestrel with mutual TLS for inter-node gRPC communication.
/// </summary>
public static class MtlsKestrelExtensions
{
    /// <summary>
    /// Configures Kestrel's HTTPS defaults to use the node certificate from <paramref name="options"/>
    /// and to require a client certificate validated against the configured trusted CA.
    /// When <see cref="TlsOptions.AllowInsecure"/> is <see langword="true"/>, client certificates
    /// are not required and server certificate validation is skipped on the client side.
    /// </summary>
    /// <param name="builder">The web host builder to configure.</param>
    /// <param name="options">TLS configuration including the node certificate and trusted CA.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="TlsOptions.AllowInsecure"/> is <see langword="false"/> and no server
    /// certificate is configured.
    /// </exception>
    public static IWebHostBuilder UseCachePeerMtls(this IWebHostBuilder builder, TlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        return builder.ConfigureKestrel(kestrel =>
        {
            kestrel.ConfigureHttpsDefaults(https =>
            {
                X509Certificate2? nodeCert = options.ResolveNodeCertificate();

                if (!options.AllowInsecure)
                {
                    if (nodeCert is null)
                    {
                        throw new InvalidOperationException(
                            "TlsOptions: a node certificate is required when AllowInsecure is false. " +
                            "Set TlsOptions.NodeCertificate or TlsOptions.CertificatePath.");
                    }

                    https.ServerCertificate = nodeCert;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (cert, _, _) =>
                        ValidateAgainstTrustedCa(cert, options.ResolveTrustedCa());
                }
                else if (nodeCert is not null)
                {
                    // Insecure mode: still present a cert if one was provided, but don't require the client to.
                    https.ServerCertificate = nodeCert;
                    https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
                }
            });
        });
    }

    private static bool ValidateAgainstTrustedCa(X509Certificate2 cert, X509Certificate2? trustedCa)
    {
        if (trustedCa is null)
        {
            // No CA pinned — accept any structurally valid cert.
            return true;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedCa);
        return chain.Build(cert);
    }
}
