// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Counts the commands EF Core sends while <see cref="Counting"/> is on (each one a round trip to the database) and the
/// statements in them, by verb and table, so a test can hold a write path to a fixed number of statements however many
/// objects it handles (#2669).
/// </summary>
/// <remarks>
/// Only commands EF Core executes are seen: a raw <c>NpgsqlCommand</c> a repository issues on the connection itself
/// (the generated-value retirement, for one) is not. Statements are split on semicolons, which holds for the SQL EF
/// Core and the repositories write, none of which carries a semicolon in a literal.
/// </remarks>
public sealed partial class SqlStatementCounter : DbCommandInterceptor
{
    public bool Counting { get; set; }

    public int Commands { get; private set; }

    public Dictionary<string, int> Statements { get; } = new(StringComparer.Ordinal);

    public int StatementCount => Statements.Values.Sum();

    public void Reset()
    {
        Commands = 0;
        Statements.Clear();
    }

    /// <summary>
    /// The statements counted, largest count first, for an assertion message.
    /// </summary>
    public string Describe() =>
        $"{Commands} command(s), {StatementCount} statement(s): " +
        string.Join(", ", Statements.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key} x{s.Value}"));

    [GeneratedRegex(@"^\s*(\w+)[^""]*""(\w+)""", RegexOptions.Singleline)]
    private static partial Regex VerbAndTable();

    private void Record(DbCommand command)
    {
        if (!Counting)
            return;

        Commands++;
        foreach (var statement in command.CommandText.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var match = VerbAndTable().Match(statement);
            var key = match.Success
                ? $"{match.Groups[1].Value.ToUpperInvariant()} {match.Groups[2].Value}"
                : statement[..Math.Min(40, statement.Length)];
            Statements[key] = Statements.GetValueOrDefault(key) + 1;
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }
}
