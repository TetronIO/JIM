// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Interfaces;

/// <summary>
/// Encrypts and decrypts synchronised passwords held in the Password Synchronisation queue (#1119,
/// requirement 7), under a Data Protection purpose distinct from the one connector credentials use.
/// <para>
/// Deliberately a separate interface from <see cref="ICredentialProtectionService"/> rather than more members on
/// it, for two reasons. The narrow one is that almost nothing in JIM handles queued passwords, so almost nothing
/// should have to declare that it can: the credential interface is implemented across configuration capture,
/// seeding, schema handling and the API, none of which has any business decrypting somebody's password. The
/// broader one is that the split mirrors the cryptographic split it describes; a caller that holds one of these
/// cannot reach the other's ciphertext, which is precisely the property the separate purpose buys.
/// </para>
/// <para>
/// <see cref="JIM.Application.Services.CredentialProtectionService"/> implements both, because they share a
/// provider, a key ring and a prefix convention. Holding both is a property of that class, not of its callers.
/// </para>
/// </summary>
public interface IPasswordProtectionService
{
    /// <summary>
    /// Encrypts a password for storage in the Password Synchronisation queue.
    /// </summary>
    /// <param name="password">The password to encrypt.</param>
    /// <returns>
    /// The encrypted password with its version prefix, or the original value if null or empty. An
    /// already-password-protected value is returned unchanged, to prevent double-encryption.
    /// </returns>
    string? ProtectPassword(string? password);

    /// <summary>
    /// Decrypts a password encrypted by <see cref="ProtectPassword"/>.
    /// </summary>
    /// <param name="protectedPassword">The encrypted password.</param>
    /// <returns>The decrypted password, or the original value if null or empty.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Thrown when the value carries no password prefix, and when decryption fails. Unlike
    /// <see cref="JIM.Models.Interfaces.ICredentialProtection.Unprotect"/>, an unprefixed value is never passed
    /// through as plain text: no password has ever been stored in clear, so one arriving without the prefix is
    /// corruption or tampering, and returning it would hand the caller something it would then transmit to a
    /// target system as though JIM had encrypted it.
    /// </exception>
    string? UnprotectPassword(string? protectedPassword);

    /// <summary>
    /// Checks whether a value carries the synchronised-password encryption prefix.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <returns>
    /// True if the value is password-protected; false otherwise, including for credential-protected values.
    /// </returns>
    bool IsPasswordProtected(string? value);
}
