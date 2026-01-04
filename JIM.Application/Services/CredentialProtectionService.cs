using Microsoft.AspNetCore.DataProtection;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Implementation of credential protection using ASP.NET Core Data Protection.
/// Encrypts sensitive values with a versioned prefix for future-proofing.
/// </summary>
public class CredentialProtectionService : ICredentialProtectionService
{
    /// <summary>
    /// Prefix for encrypted values. Format: $JIM$v1$[encrypted-data]
    /// - $JIM$ identifies this as a JIM-encrypted value
    /// - v1 is the version for future algorithm changes
    /// </summary>
    private const string EncryptionPrefix = "$JIM$v1$";

    /// <summary>
    /// Purpose string for Data Protection. Used to isolate credential encryption
    /// from other Data Protection uses in the application.
    /// </summary>
    private const string Purpose = "JIM.Credentials.v1";

    private readonly IDataProtector _protector;

    /// <summary>
    /// Initialises a new instance of the CredentialProtectionService.
    /// </summary>
    /// <param name="provider">The Data Protection provider from DI.</param>
    public CredentialProtectionService(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    /// <inheritdoc />
    public string? Protect(string? plainText)
    {
        // Null or empty values don't need encryption
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        // Already encrypted - return as-is to prevent double-encryption
        if (IsProtected(plainText))
        {
            Log.Verbose("CredentialProtectionService.Protect: Value already encrypted, returning as-is");
            return plainText;
        }

        try
        {
            var encrypted = _protector.Protect(plainText);
            var result = $"{EncryptionPrefix}{encrypted}";
            Log.Verbose("CredentialProtectionService.Protect: Successfully encrypted credential");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CredentialProtectionService.Protect: Failed to encrypt credential");
            throw;
        }
    }

    /// <inheritdoc />
    public string? Unprotect(string? protectedData)
    {
        // Null or empty values don't need decryption
        if (string.IsNullOrEmpty(protectedData))
            return protectedData;

        // Not encrypted (plain text) - return as-is for migration support
        // This allows existing plain-text passwords to work until they're re-saved
        if (!IsProtected(protectedData))
        {
            Log.Verbose("CredentialProtectionService.Unprotect: Value not encrypted, returning as-is (migration support)");
            return protectedData;
        }

        try
        {
            // Remove the prefix to get the actual encrypted data
            var cipherText = protectedData[EncryptionPrefix.Length..];
            var result = _protector.Unprotect(cipherText);
            Log.Verbose("CredentialProtectionService.Unprotect: Successfully decrypted credential");
            return result;
        }
        catch (Exception ex)
        {
            // Log error but never log the actual value
            Log.Error(ex, "CredentialProtectionService.Unprotect: Failed to decrypt credential. " +
                "This may indicate the encryption key has been changed or deleted.");
            throw;
        }
    }

    /// <inheritdoc />
    public bool IsProtected(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(EncryptionPrefix, StringComparison.Ordinal);
    }
}
