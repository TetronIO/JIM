// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    /// <returns>The decrypted credential, or the original value if null or empty.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Thrown when the value is not an encrypted credential (it carries no encryption prefix), and when decryption
    /// fails. A credential is never passed through as plain text: every credential JIM stores is encrypted, so one
    /// arriving unencrypted was stored without encryption or has been altered.
    /// </exception>
    string? Unprotect(string? protectedData);
}
