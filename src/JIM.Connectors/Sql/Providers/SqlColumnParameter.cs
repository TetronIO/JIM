// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Pairs a column with the parameter carrying its value. The two travel together so a generated
/// statement can never drift out of step between its column list and its value list.
/// </summary>
/// <param name="ColumnName">The unquoted column name; the provider quotes it.</param>
/// <param name="ParameterName">The bare parameter name; the provider adds the dialect's prefix.</param>
internal sealed record SqlColumnParameter(string ColumnName, string ParameterName);
