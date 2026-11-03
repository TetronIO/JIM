// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Encryption;

/// <summary>A WinRM message could not be encrypted or decrypted, or was not encrypted when it had to be.</summary>
public sealed class WinRMEncryptionException : Exception
{
    public WinRMEncryptionException(string message) : base(message)
    {
    }

    public WinRMEncryptionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
