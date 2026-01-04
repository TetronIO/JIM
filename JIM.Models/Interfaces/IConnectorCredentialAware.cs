namespace JIM.Models.Interfaces;

/// <summary>
/// Connectors that need to use encrypted credentials can implement this interface
/// to receive the credential protection service from JIM.
/// </summary>
public interface IConnectorCredentialAware
{
    /// <summary>
    /// Sets the credential protection service for this connector.
    /// Called by JIM before OpenImportConnection/OpenExportConnection.
    /// The connector should use this service to decrypt credentials before use.
    /// </summary>
    void SetCredentialProtection(ICredentialProtection? credentialProtection);
}
