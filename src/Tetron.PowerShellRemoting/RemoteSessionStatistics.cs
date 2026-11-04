// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting;

/// <summary>
/// What one remote session has cost so far, for logging at the end of a batch: how often it logged in to the KDC,
/// how many authenticated connections it opened, how many commands it ran, and the largest messages it exchanged. Read
/// it with
/// <see cref="RemoteSessionFactory.GetStatistics"/>.
/// </summary>
public sealed class RemoteSessionStatistics
{
    private long _kerberosLogins;
    private long _connectionsOpened;
    private long _commandsRun;
    private long _largestRequestBytes;
    private long _largestResponseBytes;

    /// <summary>Ticket-granting tickets obtained with the password.</summary>
    public long KerberosLogins => Interlocked.Read(ref _kerberosLogins);

    /// <summary>HTTP connections authenticated to the endpoint, each with its own Kerberos handshake.</summary>
    public long ConnectionsOpened => Interlocked.Read(ref _connectionsOpened);

    /// <summary>Commands (pipelines) the session has started.</summary>
    public long CommandsRun => Interlocked.Read(ref _commandsRun);

    /// <summary>The largest WS-Management message sent, in bytes before encryption.</summary>
    public long LargestRequestBytes => Interlocked.Read(ref _largestRequestBytes);

    /// <summary>The largest WS-Management message received, in bytes after decryption.</summary>
    public long LargestResponseBytes => Interlocked.Read(ref _largestResponseBytes);

    internal void RecordKerberosLogin() => Interlocked.Increment(ref _kerberosLogins);

    internal void RecordConnectionOpened() => Interlocked.Increment(ref _connectionsOpened);

    internal void RecordCommand() => Interlocked.Increment(ref _commandsRun);

    internal void RecordMessage(long requestBytes, long responseBytes)
    {
        RaiseTo(ref _largestRequestBytes, requestBytes);
        RaiseTo(ref _largestResponseBytes, responseBytes);
    }

    private static void RaiseTo(ref long largest, long candidate)
    {
        var current = Interlocked.Read(ref largest);
        while (candidate > current)
        {
            var seen = Interlocked.CompareExchange(ref largest, candidate, current);
            if (seen == current)
                return;
            current = seen;
        }
    }
}
