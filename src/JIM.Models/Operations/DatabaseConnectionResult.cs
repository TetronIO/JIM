// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Operations;

/// <summary>
/// The outcome of one attempt to reach the database server (<c>IRepository.TryConnectAsync</c>). A failure
/// here is always one worth retrying; a failure that retrying cannot fix (rejected credentials, a refused TLS
/// handshake) is thrown instead, so the service stops with the real error rather than waiting it out.
/// </summary>
/// <param name="IsConnected">Whether the server accepted a connection.</param>
/// <param name="FailureReason">Why it did not, naming the server, for the administrator reading the log; null on success.</param>
public sealed record DatabaseConnectionResult(bool IsConnected, string? FailureReason)
{
    public static DatabaseConnectionResult Connected { get; } = new(true, null);

    public static DatabaseConnectionResult Failed(string reason) => new(false, reason);
}
