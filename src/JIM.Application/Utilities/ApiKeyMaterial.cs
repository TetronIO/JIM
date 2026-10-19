// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;

namespace JIM.Application.Utilities;

/// <summary>
/// Generates API key secrets and hashes them for storage. JIM stores only the hash; the secret is shown once, when
/// the key is created.
/// </summary>
public static class ApiKeyMaterial
{
    /// <summary>
    /// The prefix every API key secret starts with.
    /// </summary>
    public const string Prefix = "jim_ak_";

    /// <summary>
    /// Generates a new secret: the prefix followed by 32 random bytes as lower-case hex.
    /// </summary>
    /// <returns>The full secret, and its first 12 characters for display (the prefix and four random characters).</returns>
    public static (string FullKey, string KeyPrefix) Generate()
    {
        var randomPart = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var fullKey = $"{Prefix}{randomPart}";
        return (fullKey, fullKey[..12]);
    }

    /// <summary>
    /// Hashes a secret with SHA-256, as stored in <c>ApiKey.KeyHash</c>.
    /// </summary>
    public static string Hash(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
}
