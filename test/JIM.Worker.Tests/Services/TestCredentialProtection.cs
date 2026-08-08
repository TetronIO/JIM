// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Credential protection for tests, round-tripping through a recognisable prefix and base64.
/// <para>
/// Deliberately not a no-op passthrough: the ciphertext never literally contains the plaintext, so a test can
/// assert that neither the password nor the stored value reached a message or a log line. That is the property
/// the real service has and the one worth preserving in a double.
/// </para>
/// </summary>
internal sealed class TestCredentialProtection : ICredentialProtection
{
    private const string Prefix = "$JIM$v1$";

    /// <summary>
    /// Stands in for an encryption key that has been rotated or lost, which is what the real service throws on.
    /// </summary>
    public bool FailToDecrypt { get; set; }

    public string? Protect(string? plainText) =>
        string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

    public string? Unprotect(string? protectedData)
    {
        if (string.IsNullOrEmpty(protectedData) || !protectedData.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedData;

        if (FailToDecrypt)
            throw new CryptographicException("The key used to protect this payload could not be found.");

        return Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));
    }
}
