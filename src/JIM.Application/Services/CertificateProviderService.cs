using JIM.Models.Interfaces;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Application.Services;

/// <summary>
/// Provides trusted certificates from the JIM certificate store to connectors.
/// Implements ICertificateProvider for use with IConnectorCertificateAware connectors.
/// </summary>
public class CertificateProviderService : ICertificateProvider
{
    private readonly JimApplication _application;

    public CertificateProviderService(JimApplication application)
    {
        _application = application;
    }

    /// <summary>
    /// Gets all enabled trusted certificates as X509Certificate2 objects.
    /// </summary>
    public async Task<List<X509Certificate2>> GetTrustedCertificatesAsync()
    {
        return await _application.Certificates.GetTrustedCertificatesAsync();
    }
}
