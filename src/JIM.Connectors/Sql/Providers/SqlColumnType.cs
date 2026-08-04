// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// A column's declared SQL type as reported by a schema catalogue, in the form the type mapper needs.
/// </summary>
/// <param name="TypeName">
/// The catalogue's type name, verbatim. May carry a size in parentheses (Oracle reports
/// "TIMESTAMP(6) WITH TIME ZONE"); the mapper normalises that away.
/// </param>
/// <param name="Precision">Total digits for an exact numeric type, where the catalogue reports one.</param>
/// <param name="Scale">Digits to the right of the decimal point, where the catalogue reports one.</param>
/// <param name="MaxLength">Declared length in bytes or characters, where the catalogue reports one.</param>
internal sealed record SqlColumnType(string TypeName, int? Precision = null, int? Scale = null, int? MaxLength = null);
