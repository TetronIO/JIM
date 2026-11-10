// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Npgsql;

namespace JIM.PostgresData;

/// <summary>
/// Turns PostgreSQL's JIT compilation off for the rest of a transaction, for statements whose planner estimates are
/// known to be inflated, where compiling them costs more than the time it would save.
/// </summary>
/// <remarks>
/// <para>
/// Sent first in a batch that runs outside a transaction block, it holds for the batch's own implicit transaction, up
/// to the batch's Sync, and no longer; inside a transaction it holds until that transaction ends.
/// </para>
/// <para>
/// <c>set_config(..., true)</c> rather than <c>SET LOCAL</c>, which has the same scope but, as the first statement of a
/// batch outside a transaction block, logs <c>WARNING: SET LOCAL can only be used in transaction blocks</c> every time
/// (#2849). Unlike <c>SET</c>, it returns a row: a reader steps past it with <see cref="SkipDisableResultAsync"/> before
/// reading the batch's own results, so the setting's value is never read as one of them.
/// </para>
/// </remarks>
internal static class TransactionJit
{
    /// <summary>The statement that turns JIT off for the rest of the transaction; send it first in the batch.</summary>
    internal const string DisableSql = "SELECT set_config('jit', 'off', true)";

    /// <summary>
    /// Reads the result of <see cref="DisableSql"/>, which the reader must be on (a batch's first result), and moves to
    /// the next result.
    /// </summary>
    /// <returns>Whether the batch has another result.</returns>
    /// <exception cref="InvalidOperationException">The reader is on any other result: the batch's results are out of
    /// step with its statements, and reading on would take one statement's result for another's.</exception>
    internal static async Task<bool> SkipDisableResultAsync(NpgsqlDataReader reader)
    {
        if (reader.FieldCount != 1 || reader.GetName(0) != "set_config" || !await reader.ReadAsync() || reader.GetString(0) != "off")
            throw new InvalidOperationException("The batch's first result is not JIT being turned off.");

        return await reader.NextResultAsync();
    }
}
