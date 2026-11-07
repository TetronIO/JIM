// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Records each reading command a context runs, with copies of its parameters, so a test can run each again under
/// EXPLAIN (<see cref="PlanRowsRead"/>).
/// </summary>
internal sealed class CommandRecordingInterceptor : DbCommandInterceptor
{
    public List<(string Text, List<NpgsqlParameter> Parameters)> Commands { get; } = [];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToList()));
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
