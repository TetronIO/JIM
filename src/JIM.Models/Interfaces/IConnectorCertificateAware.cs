namespace JIM.Models.Interfaces;

/// <summary>
/// Connectors that require SSL/TLS certificate validation can implement this interface
/// to receive trusted certificates from the JIM certificate store.
/// </summary>
public interface IConnectorCertificateAware
{
    /// <summary>
    /// Sets the certificate provider for this connector.
    /// Called by JIM before OpenImportConnection/OpenExportConnection.
    /// </summary>
    void SetCertificateProvider(ICertificateProvider? certificateProvider);
}
