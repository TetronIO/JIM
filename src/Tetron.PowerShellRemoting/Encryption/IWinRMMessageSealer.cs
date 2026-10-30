// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Encryption;

/// <summary>
/// Encrypts and decrypts WinRM message payloads with an established security context (Kerberos). WinRM sends the
/// token header separately from the encrypted data, which is why this is not a single-buffer wrap.
/// </summary>
public interface IWinRMMessageSealer
{
    /// <summary>Encrypts <paramref name="plaintext"/>, returning the token header and the encrypted data separately.</summary>
    SealedMessage Wrap(ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypts and verifies a message whose token header and encrypted data arrived separately.</summary>
    /// <exception cref="WinRMEncryptionException">The message failed integrity or could not be decrypted.</exception>
    byte[] Unwrap(ReadOnlySpan<byte> header, ReadOnlySpan<byte> data);
}
