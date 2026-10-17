// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Staging;

namespace JIM.Connectors.Sql;

/// <summary>
/// One row of <see cref="SqlExportRefusalClassifier"/>'s table.
/// </summary>
/// <param name="ServerFamily">The database whose refusal this row reads.</param>
/// <param name="DatabaseType">The provider whose error numbers the row reads; null for a row keyed on SQLSTATE.</param>
/// <param name="FirstErrorNumber">The first of the provider's error numbers the row covers; null for a row keyed on
/// SQLSTATE.</param>
/// <param name="LastErrorNumber">The last of the provider's error numbers the row covers, the same as
/// <paramref name="FirstErrorNumber"/> for a single number.</param>
/// <param name="SqlState">The SQLSTATE the row applies to, whatever the provider: five characters for one condition,
/// or two for its whole class. Null for a row keyed on error numbers.</param>
/// <param name="ErrorType">What the refusal is reported as.</param>
/// <param name="Description">What the row recognises, for whoever reads the table.</param>
internal sealed record SqlExportRefusalRule(
    string ServerFamily,
    SqlDatabaseType? DatabaseType,
    int? FirstErrorNumber,
    int? LastErrorNumber,
    string? SqlState,
    ConnectedSystemExportErrorType ErrorType,
    string Description);
