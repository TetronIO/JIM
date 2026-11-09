// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Records each non-query command a context runs, with copies of its parameters, so a test can run each again under
/// EXPLAIN. The writing sibling of <see cref="CommandRecordingInterceptor"/>.
/// </summary>
internal sealed class NonQueryRecordingInterceptor : DbCommandInterceptor
{
    public List<(string Text, List<NpgsqlParameter> Parameters)> Commands { get; } = [];

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToList()));
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
