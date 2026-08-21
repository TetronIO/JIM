// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using JIM.Application.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Implementation of credential protection using ASP.NET Core Data Protection.
/// Encrypts sensitive values with a versioned prefix for future-proofing.
/// </summary>
public class CredentialProtectionService : ICredentialProtectionService, IPasswordProtectionService
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

    /// <summary>
    /// Prefix for encrypted synchronised passwords. Format: $JIMPW$v1$[encrypted-data]
    /// <para>
    /// Distinct from <see cref="EncryptionPrefix"/> so that a stored value declares which protector can read it,
    /// and so a queued password and a Connected System's bind credential are never mistaken for one another on
    /// inspection.
    /// </para>
    /// </summary>
    private const string PasswordEncryptionPrefix = "$JIMPW$v1$";

    /// <summary>
    /// Purpose string for synchronised passwords (#1119, requirement 7), deliberately distinct from
    /// <see cref="Purpose"/>.
    /// <para>
    /// Connector settings and queued passwords are different kinds of secret with different blast radii: a
    /// connector credential is one account JIM holds, a queued password is a person's own credential across every
    /// system they use. Data Protection purposes are cryptographically separating, so a protector obtained for
    /// one cannot read the other's output even though both derive from the same key ring.
    /// </para>
    /// <para>
    /// Note that <c>SyncRuleInitialPassword.StaticPasswordEncryptedValue</c> stays under
    /// <see cref="Purpose"/> where it was written; nothing re-encrypts on this change.
    /// </para>
    /// </summary>
    private const string PasswordPurpose = "JIM.PasswordSync.v1";

    private readonly IDataProtector _protector;
    private readonly IDataProtector _passwordProtector;

    /// <summary>
    /// Initialises a new instance of the CredentialProtectionService.
    /// </summary>
    /// <param name="provider">The Data Protection provider from DI.</param>
    public CredentialProtectionService(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
        _passwordProtector = provider.CreateProtector(PasswordPurpose);
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

        // A value belonging to the password purpose is definitively not a credential, and the migration
        // passthrough below would hand back its ciphertext as though it were plain text: a caller would then
        // transmit that ciphertext to a target system as a credential. Refuse instead of guessing.
        if (IsPasswordProtected(protectedData))
        {
            Log.Error("CredentialProtectionService.Unprotect: Value is protected under the password purpose, " +
                      "not the credential purpose. Use UnprotectPassword for password payloads.");
            throw new CryptographicException(
                "The stored value is a JIM-protected password, not a credential, and cannot be decrypted here.");
        }

        // Not encrypted (plain text) - return as-is for migration support
        // This allows existing plain-text credentials to work until they're re-saved
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

    /// <inheritdoc />
    public string? ProtectPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return password;

        if (IsPasswordProtected(password))
        {
            Log.Verbose("CredentialProtectionService.ProtectPassword: Value already encrypted, returning as-is");
            return password;
        }

        try
        {
            return $"{PasswordEncryptionPrefix}{_passwordProtector.Protect(password)}";
        }
        catch (Exception ex)
        {
            // Never log the value, nor its length: a length is a meaningful hint about a password.
            Log.Error(ex, "CredentialProtectionService.ProtectPassword: Failed to encrypt a password");
            throw;
        }
    }

    /// <inheritdoc />
    public string? UnprotectPassword(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword))
            return protectedPassword;

        // Deliberately unlike Unprotect, which returns an unprefixed value as-is to support credentials stored
        // before encryption existed. No password has ever been stored in clear, so an unprefixed value here is
        // corruption or tampering; returning it would hand the caller something it would then transmit to a
        // directory as though JIM had encrypted it.
        if (!IsPasswordProtected(protectedPassword))
        {
            Log.Error("CredentialProtectionService.UnprotectPassword: Value is not password-protected. " +
                      "A stored password without the expected prefix indicates corruption or tampering.");
            throw new CryptographicException(
                "The stored value is not a JIM-protected password. It may have been corrupted or altered.");
        }

        try
        {
            return _passwordProtector.Unprotect(protectedPassword[PasswordEncryptionPrefix.Length..]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CredentialProtectionService.UnprotectPassword: Failed to decrypt a password. " +
                          "This may indicate the encryption key has been changed or deleted.");
            throw;
        }
    }

    /// <inheritdoc />
    public bool IsPasswordProtected(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(PasswordEncryptionPrefix, StringComparison.Ordinal);
    }
}
