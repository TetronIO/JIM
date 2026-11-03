// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting;

/// <summary>
/// What one remote session has cost so far, for logging at the end of a batch: how often it logged in to the KDC,
/// how many authenticated connections it opened, and how many commands it ran. Read it with
/// <see cref="RemoteSessionFactory.GetStatistics"/>.
/// </summary>
public sealed class RemoteSessionStatistics
{
    private long _kerberosLogins;
    private long _connectionsOpened;
    private long _commandsRun;

    /// <summary>Ticket-granting tickets obtained with the password.</summary>
    public long KerberosLogins => Interlocked.Read(ref _kerberosLogins);

    /// <summary>HTTP connections authenticated to the endpoint, each with its own Kerberos handshake.</summary>
    public long ConnectionsOpened => Interlocked.Read(ref _connectionsOpened);

    /// <summary>Commands (pipelines) the session has started.</summary>
    public long CommandsRun => Interlocked.Read(ref _commandsRun);

    internal void RecordKerberosLogin() => Interlocked.Increment(ref _kerberosLogins);

    internal void RecordConnectionOpened() => Interlocked.Increment(ref _connectionsOpened);

    internal void RecordCommand() => Interlocked.Increment(ref _commandsRun);
}
