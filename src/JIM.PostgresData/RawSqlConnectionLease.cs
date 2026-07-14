// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data;
using System.Data.Common;

namespace JIM.PostgresData;

/// <summary>
/// Opens a database connection if it is not already open, and closes it again on disposal only if
/// this lease opened it. Every raw-SQL helper that calls <c>GetDbConnection()</c> must acquire one
/// of these (or use the equivalent wasOpen try/finally): EF Core only auto-closes connections it
/// opened itself, so a connection opened by repository code stays checked out of the Npgsql pool
/// until the DbContext is disposed. On per-batch contexts that pins one pooled connection per
/// batch and exhausts the pool - the Scale200k10kGroups parallel export failure of 2026-07-13,
/// where batch 29 onwards all failed with "The connection pool has been exhausted".
/// A connection that was already open when acquired (for example inside an EF transaction) is
/// left open on disposal.
/// </summary>
internal sealed class RawSqlConnectionLease : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly bool _wasOpen;

    private RawSqlConnectionLease(DbConnection connection, bool wasOpen)
    {
        _connection = connection;
        _wasOpen = wasOpen;
    }

    public static async Task<RawSqlConnectionLease> AcquireAsync(DbConnection connection)
    {
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync();
        return new RawSqlConnectionLease(connection, wasOpen);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_wasOpen)
            await _connection.CloseAsync();
    }
}
