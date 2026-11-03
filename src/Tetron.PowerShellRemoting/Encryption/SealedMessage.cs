// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Encryption;

/// <summary>A message encrypted by an <see cref="IWinRMMessageSealer"/>.</summary>
/// <param name="Header">The security token header, sent ahead of the data.</param>
/// <param name="Data">The encrypted data.</param>
/// <param name="PaddingLength">Padding the cipher added and did not send; zero for AES.</param>
public sealed record SealedMessage(byte[] Header, byte[] Data, int PaddingLength);
