// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// An open connection for a repository under test that records every notice the server sends it, with each statement's
/// plan among them: <c>auto_explain</c> reports the plan as a notice, with the planner settings the statement ran under
/// (<c>Settings: jit = 'off'</c>), so a test can see what a statement inside a repository's own batch ran with.
/// </summary>
/// <remarks>
/// <c>LOAD 'auto_explain'</c> needs a superuser, which the <c>JIM_TEST_RESET_*</c> databases connect as. Unpooled, so
/// the session settings cannot leak to another test's connection. Hand <see cref="Connection"/> to the context under
/// test; repositories leave a connection they did not open open.
/// </remarks>
internal sealed class StatementPlanRecorder : IAsyncDisposable
{
    private readonly ConcurrentQueue<PostgresNotice> _notices = new();

    private StatementPlanRecorder(NpgsqlConnection connection)
    {
        Connection = connection;
        Connection.Notice += (_, e) => _notices.Enqueue(e.Notice);
    }

    public NpgsqlConnection Connection { get; }

    /// <summary>Every notice and warning the server has sent, in order.</summary>
    public IReadOnlyList<PostgresNotice> Notices => [.. _notices];

    public static async Task<StatementPlanRecorder> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        var recorder = new StatementPlanRecorder(connection);
        await using var setUp = new NpgsqlCommand("""
            LOAD 'auto_explain';
            SET auto_explain.log_min_duration = 0;
            SET auto_explain.log_level = notice;
            SET auto_explain.log_settings = on;
            SET client_min_messages = notice;
            """, connection);
        await setUp.ExecuteNonQueryAsync();
        return recorder;
    }

    /// <summary>The plans of the statements whose text contains <paramref name="queryTextFragment"/>, in order.</summary>
    public List<string> PlansOf(string queryTextFragment) =>
        Notices.Select(n => n.MessageText)
            .Where(m => m.Contains("Query Text:", StringComparison.Ordinal) && m.Contains(queryTextFragment, StringComparison.Ordinal))
            .ToList();

    /// <summary>The setting's value on this connection, outside any transaction.</summary>
    public async Task<string> ShowAsync(string setting)
    {
        await using var show = new NpgsqlCommand("SELECT current_setting(@setting)", Connection);
        show.Parameters.AddWithValue("setting", setting);
        return (string)(await show.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
