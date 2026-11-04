// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Encryption;

/// <summary>An HTTP body and the <c>Content-Type</c> to send it with.</summary>
public sealed record EncodedMessage(byte[] Body, string ContentType);
