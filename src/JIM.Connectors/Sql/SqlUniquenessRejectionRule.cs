// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;

namespace JIM.Connectors.Sql;

/// <summary>
/// One row of <see cref="SqlUniquenessRejectionClassifier"/>'s table.
/// </summary>
/// <param name="ServerFamily">The database whose refusal this row reads.</param>
/// <param name="DatabaseType">The provider whose error number the row reads; null for a row keyed on SQLSTATE.</param>
/// <param name="ErrorNumber">The provider's error number; null for a row keyed on SQLSTATE.</param>
/// <param name="SqlState">The SQLSTATE the row applies to, whatever the provider; null for a row keyed on an error
/// number.</param>
/// <param name="Column">The column the refusal is attributed to, read from the database's message; null when it
/// named none.</param>
/// <param name="Description">What the row recognises, for whoever reads the table.</param>
internal sealed record SqlUniquenessRejectionRule(
    string ServerFamily,
    SqlDatabaseType? DatabaseType,
    int? ErrorNumber,
    string? SqlState,
    Func<string, string?> Column,
    string Description);
