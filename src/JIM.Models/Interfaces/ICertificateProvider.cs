using System.Security.Cryptography.X509Certificates;

namespace JIM.Models.Interfaces;

/// <summary>
/// Provides access to trusted certificates for connector SSL/TLS validation.
/// </summary>
public interface ICertificateProvider
{
    /// <summary>
    /// Gets all enabled trusted certificates as X509Certificate2 objects.
    /// </summary>
    Task<List<X509Certificate2>> GetTrustedCertificatesAsync();
}
