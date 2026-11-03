// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.WSMan;

/// <summary>What every WS-Management request in one PowerShell session carries.</summary>
/// <param name="Endpoint">The WinRM endpoint, for example <c>http://ex1.panoply.local/PowerShell/</c>.</param>
/// <param name="ResourceUri">The session configuration, for example <c>http://schemas.microsoft.com/powershell/Microsoft.Exchange</c>.</param>
/// <param name="SessionId">Identifies the client session to the server across requests.</param>
/// <param name="MaxEnvelopeSize">The largest response, in bytes, the client accepts.</param>
/// <param name="OperationTimeout">How long the server may hold a request (a Receive waiting for output) before timing out.</param>
public sealed record WSManSessionSettings(Uri Endpoint, string ResourceUri, Guid SessionId, int MaxEnvelopeSize, TimeSpan OperationTimeout);
