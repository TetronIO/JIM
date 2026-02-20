using JIM.Models.Interfaces;

namespace JIM.Application.Services;

/// <summary>
/// Service for encrypting and decrypting credential data at rest.
/// Uses ASP.NET Core Data Protection with AES-256-GCM for secure encryption.
/// Extends ICredentialProtection for use by connectors.
/// </summary>
public interface ICredentialProtectionService : ICredentialProtection
{
    /// <summary>
    /// Encrypts a plain text credential for secure storage.
    /// </summary>
    /// <param name="plainText">The credential to encrypt.</param>
    /// <returns>
    /// The encrypted credential with version prefix, or the original value if null/empty.
    /// Already-encrypted values are returned unchanged to prevent double-encryption.
    /// </returns>
    string? Protect(string? plainText);

    /// <summary>
    /// Checks whether a value appears to be encrypted (has the JIM encryption prefix).
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <returns>True if the value has the encryption prefix, false otherwise.</returns>
    bool IsProtected(string? value);
}
