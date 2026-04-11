using DistCache.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace DistCache.Admin.Extensions;

/// <summary>
/// Extension methods for creating <see cref="TlsOptions"/> from <see cref="IConfiguration"/>.
/// </summary>
public static class TlsOptionsExtensions
{
    /// <summary>
    /// Creates a <see cref="TlsOptions"/> instance from an <see cref="IConfigurationSection"/>.
    /// Reads <c>CertificatePath</c>, <c>CertificatePassword</c>, <c>TrustedCaCertificatePath</c>,
    /// and <c>AllowInsecure</c> keys.
    /// </summary>
    /// <param name="section">The configuration section to bind from.</param>
    /// <returns>A populated <see cref="TlsOptions"/> instance.</returns>
    public static TlsOptions ToTlsOptions(this IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return new TlsOptions
        {
            CertificatePath = section[nameof(TlsOptions.CertificatePath)],
            CertificatePassword = section[nameof(TlsOptions.CertificatePassword)],
            TrustedCaCertificatePath = section[nameof(TlsOptions.TrustedCaCertificatePath)],
            AllowInsecure = section.GetValue<bool>(nameof(TlsOptions.AllowInsecure)),
        };
    }
}
