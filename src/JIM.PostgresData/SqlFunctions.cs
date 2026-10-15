// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData;

/// <summary>
/// PostgreSQL built-in functions mapped for LINQ queries (registered in <see cref="JimDbContext"/>), so a query can apply
/// them to a parameter in SQL rather than in .NET.
/// </summary>
public static class SqlFunctions
{
    /// <summary>
    /// PostgreSQL's <c>lower()</c>. An identity lookup ignoring case compares <c>lower(column)</c> with
    /// <c>Lower(value)</c>, so the database folds both sides by the same rule (#2051): its own <c>lower()</c> follows the
    /// database's character type, which .NET's folding need not agree with.
    /// </summary>
    public static string Lower(string value) =>
        throw new NotSupportedException($"{nameof(SqlFunctions)}.{nameof(Lower)} is translated to SQL and cannot be called directly.");
}
