// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Text;

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Maps a column's SQL type onto a JIM <see cref="AttributeDataType"/>.
/// <para>
/// Most type families mean the same thing on every server, so they live in one table; the handful that
/// do not are resolved per dialect before that table is consulted. Reference attributes are never
/// inferred from a type: a column holding another object type's anchor looks exactly like any other
/// column, so it is explicit per-column configuration.
/// </para>
/// <para>
/// An unrecognised type throws. Degrading it to Text would import values JIM then compares
/// lexicographically, which is precisely the defect the Decimal attribute type was introduced to fix.
/// </para>
/// </summary>
internal static class SqlTypeMapper
{
    /// <summary>
    /// The families that mean the same thing on every supported server. Priority 2 spellings
    /// (PostgreSQL's <c>bytea</c> and <c>uuid</c>, for example) are present so adding those providers
    /// stays additive.
    /// </summary>
    private static readonly Dictionary<string, AttributeDataType> CommonTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Character data.
        ["VARCHAR"] = AttributeDataType.Text,
        ["VARCHAR2"] = AttributeDataType.Text,
        ["NVARCHAR"] = AttributeDataType.Text,
        ["NVARCHAR2"] = AttributeDataType.Text,
        ["CHAR"] = AttributeDataType.Text,
        ["NCHAR"] = AttributeDataType.Text,
        ["CHARACTER"] = AttributeDataType.Text,
        ["TEXT"] = AttributeDataType.Text,
        ["NTEXT"] = AttributeDataType.Text,
        ["CLOB"] = AttributeDataType.Text,
        ["NCLOB"] = AttributeDataType.Text,
        ["LONG"] = AttributeDataType.Text,

        // Integers narrow enough for a 32-bit value.
        ["INT"] = AttributeDataType.Number,
        ["INTEGER"] = AttributeDataType.Number,
        ["SMALLINT"] = AttributeDataType.Number,
        ["TINYINT"] = AttributeDataType.Number,

        // Integers that need 64 bits.
        ["BIGINT"] = AttributeDataType.LongNumber,

        // Two-state values.
        ["BIT"] = AttributeDataType.Boolean,
        ["BOOLEAN"] = AttributeDataType.Boolean,
        ["BOOL"] = AttributeDataType.Boolean,

        // Points in time. Offset-carrying types are normalised to UTC on import; zoneless types are
        // interpreted per the Connected System's time zone setting.
        ["DATE"] = AttributeDataType.DateTime,
        ["DATETIME"] = AttributeDataType.DateTime,
        ["DATETIME2"] = AttributeDataType.DateTime,
        ["SMALLDATETIME"] = AttributeDataType.DateTime,
        ["TIMESTAMP"] = AttributeDataType.DateTime,
        ["DATETIMEOFFSET"] = AttributeDataType.DateTime,
        ["TIMESTAMP WITH TIME ZONE"] = AttributeDataType.DateTime,
        ["TIMESTAMP WITH LOCAL TIME ZONE"] = AttributeDataType.DateTime,

        // Identifiers.
        ["UNIQUEIDENTIFIER"] = AttributeDataType.Guid,
        ["UUID"] = AttributeDataType.Guid,

        // Opaque bytes.
        ["BINARY"] = AttributeDataType.Binary,
        ["VARBINARY"] = AttributeDataType.Binary,
        ["IMAGE"] = AttributeDataType.Binary,
        ["BLOB"] = AttributeDataType.Binary,
        ["RAW"] = AttributeDataType.Binary,
        ["LONG RAW"] = AttributeDataType.Binary,
        ["BYTEA"] = AttributeDataType.Binary,

        // Exact numerics.
        ["DECIMAL"] = AttributeDataType.Decimal,
        ["DEC"] = AttributeDataType.Decimal,
        ["NUMERIC"] = AttributeDataType.Decimal,
        ["MONEY"] = AttributeDataType.Decimal,
        ["SMALLMONEY"] = AttributeDataType.Decimal,
        ["NUMBER"] = AttributeDataType.Decimal,

        // Approximate numerics. Decimal keeps numeric comparison semantics, which a Text mapping would
        // lose; the trade is that a binary-to-decimal round trip is not bit-exact, which is a
        // documented caveat rather than a defect.
        ["FLOAT"] = AttributeDataType.Decimal,
        ["REAL"] = AttributeDataType.Decimal,
        ["DOUBLE"] = AttributeDataType.Decimal,
        ["DOUBLE PRECISION"] = AttributeDataType.Decimal,
        ["BINARY_FLOAT"] = AttributeDataType.Decimal,
        ["BINARY_DOUBLE"] = AttributeDataType.Decimal
    };

    /// <summary>
    /// The date and time types that carry their own UTC offset. Every one of them maps to DateTime like
    /// its zoneless siblings, so the distinction is invisible to <see cref="Map"/>; it matters only to
    /// the value conversions either side of it.
    /// <para>
    /// The spellings are the ones each catalogue reports, normalised: SQL Server's
    /// <c>datetimeoffset</c>, and Oracle's <c>TIMESTAMP(n) WITH TIME ZONE</c> and
    /// <c>TIMESTAMP(n) WITH LOCAL TIME ZONE</c>. PostgreSQL's <c>timestamptz</c> is present on the same
    /// reasoning as the priority 2 spellings above: adding that provider stays additive.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> OffsetCarryingTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATETIMEOFFSET",
        "TIMESTAMP WITH TIME ZONE",
        "TIMESTAMP WITH LOCAL TIME ZONE",
        "TIMESTAMPTZ"
    };

    /// <summary>
    /// Whether a column states the offset of the values it holds.
    /// </summary>
    /// <remarks>
    /// This is what decides whether the Connected System's Database Time Zone applies to a value. A
    /// column carrying an offset is unambiguous at the wire level, so it needs no setting to interpret
    /// it (PRD requirement 9): import takes the instant the driver hands back, and export writes the
    /// instant JIM holds. A zoneless column states nothing, so its values are wall-clock time in the
    /// zone the administrator declared, and both directions convert through it.
    /// </remarks>
    internal static bool CarriesAnOffset(SqlColumnType columnType)
    {
        ArgumentNullException.ThrowIfNull(columnType);

        return OffsetCarryingTypes.Contains(Normalise(columnType.TypeName));
    }

    /// <summary>
    /// Maps a column's SQL type onto a JIM attribute type.
    /// </summary>
    /// <exception cref="SqlTypeMappingException">The type has no JIM equivalent.</exception>
    internal static AttributeDataType Map(SqlDatabaseType databaseType, SqlColumnType columnType, SqlTypeMappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(columnType);
        ArgumentNullException.ThrowIfNull(options);

        var normalisedTypeName = Normalise(columnType.TypeName);
        if (normalisedTypeName.Length == 0)
            throw new SqlTypeMappingException(columnType.TypeName ?? string.Empty, databaseType);

        var dialectSpecific = MapDialectSpecific(databaseType, normalisedTypeName, columnType, options);
        if (dialectSpecific.HasValue)
            return dialectSpecific.Value;

        if (CommonTypes.TryGetValue(normalisedTypeName, out var mapped))
            return mapped;

        throw new SqlTypeMappingException(columnType.TypeName, databaseType);
    }

    /// <summary>
    /// The types whose meaning depends on which server declared them, resolved before the shared table
    /// so a dialect can override a shared entry.
    /// </summary>
    private static AttributeDataType? MapDialectSpecific(
        SqlDatabaseType databaseType,
        string normalisedTypeName,
        SqlColumnType columnType,
        SqlTypeMappingOptions options)
    {
        return databaseType switch
        {
            SqlDatabaseType.SqlServer => MapSqlServerSpecific(normalisedTypeName),
            SqlDatabaseType.Oracle => MapOracleSpecific(normalisedTypeName, columnType, options),
            _ => null
        };
    }

    private static AttributeDataType? MapSqlServerSpecific(string normalisedTypeName)
    {
        // SQL Server's 'timestamp' is a row version: eight opaque bytes with no relationship to a
        // point in time. The shared table maps TIMESTAMP to DateTime because that is what it means
        // everywhere else, so this override has to come first.
        if (normalisedTypeName is "TIMESTAMP" or "ROWVERSION")
            return AttributeDataType.Binary;

        return null;
    }

    private static AttributeDataType? MapOracleSpecific(string normalisedTypeName, SqlColumnType columnType, SqlTypeMappingOptions options)
    {
        // NUMBER(1) is a perfectly ordinary number unless an administrator has said the estate stores
        // flags that way, so the reinterpretation is opt-in rather than inferred.
        if (normalisedTypeName == "NUMBER")
        {
            var isSingleDigitInteger = columnType.Precision == 1 && (columnType.Scale ?? 0) == 0;
            return options.TreatSingleDigitNumberAsBoolean && isSingleDigitInteger
                ? AttributeDataType.Boolean
                : AttributeDataType.Decimal;
        }

        // RAW(16) is just as commonly a digest as a GUID, so this is opt-in on the same reasoning.
        if (normalisedTypeName == "RAW")
        {
            return options.TreatRaw16AsGuid && columnType.MaxLength == 16
                ? AttributeDataType.Guid
                : AttributeDataType.Binary;
        }

        return null;
    }

    /// <summary>
    /// Reduces a catalogue's type name to its family: upper case, with any parenthesised size or
    /// precision removed and internal whitespace collapsed. Oracle reports "TIMESTAMP(6) WITH TIME
    /// ZONE" and "INTERVAL DAY(2) TO SECOND(6)", where the family is all that decides the mapping.
    /// </summary>
    private static string Normalise(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return string.Empty;

        var builder = new StringBuilder(typeName.Length);
        var parenthesisDepth = 0;
        var lastAppendedWasSpace = true;

        foreach (var character in typeName)
        {
            switch (character)
            {
                case '(':
                    parenthesisDepth++;
                    continue;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    continue;
            }

            if (parenthesisDepth > 0)
                continue;

            if (char.IsWhiteSpace(character))
            {
                if (!lastAppendedWasSpace)
                {
                    builder.Append(' ');
                    lastAppendedWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
            lastAppendedWasSpace = false;
        }

        return builder.ToString().Trim();
    }
}
