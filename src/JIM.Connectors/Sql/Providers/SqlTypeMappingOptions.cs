// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Per-Connected System choices that change how a column's SQL type maps onto a JIM attribute type.
/// <para>
/// Both members exist because the database cannot express the distinction: an Oracle NUMBER(1) and a
/// RAW(16) are perfectly ordinary numeric and binary columns, and only an administrator knows whether
/// a particular one is really a flag or a GUID. Guessing from the type alone would silently
/// reinterpret real data, so both default to off.
/// </para>
/// </summary>
internal sealed record SqlTypeMappingOptions
{
    /// <summary>
    /// The defaults: no reinterpretation of numeric or binary columns.
    /// </summary>
    internal static SqlTypeMappingOptions Default { get; } = new();

    /// <summary>
    /// When set, an Oracle NUMBER(1) (or NUMBER(1,0)) column maps to Boolean rather than Decimal.
    /// </summary>
    internal bool TreatSingleDigitNumberAsBoolean { get; init; }

    /// <summary>
    /// When set, an Oracle RAW(16) column maps to Guid rather than Binary. Only exactly 16 bytes
    /// qualify; a RAW of any other length can never hold a GUID whatever the configuration says.
    /// </summary>
    internal bool TreatRaw16AsGuid { get; init; }
}
