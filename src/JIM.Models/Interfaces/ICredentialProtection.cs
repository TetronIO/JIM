namespace JIM.Models.Interfaces;

/// <summary>
/// Interface for decrypting credential data.
/// This is a simplified interface for use by connectors - the full implementation
/// is in JIM.Application.Services.ICredentialProtectionService.
/// </summary>
public interface ICredentialProtection
{
    /// <summary>
    /// Decrypts a previously encrypted credential.
    /// </summary>
    /// <param name="protectedData">The encrypted credential.</param>
    /// <returns>
    /// The decrypted credential, or the original value if null/empty.
    /// Plain text values (without encryption prefix) are returned as-is for migration support.
    /// </returns>
    string? Unprotect(string? protectedData);
}
