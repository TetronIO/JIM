// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The result column names every provider's schema-catalogue queries alias to. SQL Server and Oracle
/// name their catalogue columns differently (TABLE_SCHEMA vs OWNER, and so on); aliasing both to this
/// one set keeps schema discovery free of dialect knowledge, which is the whole point of the provider
/// seam. Aliases are written unquoted and upper case so Oracle does not fold them differently.
/// </summary>
internal static class SqlCatalogueColumns
{
    internal const string SchemaName = "SCHEMA_NAME";

    internal const string ObjectName = "OBJECT_NAME";

    internal const string ColumnName = "COLUMN_NAME";

    internal const string DataTypeName = "DATA_TYPE_NAME";

    internal const string MaxLength = "MAX_LENGTH";

    internal const string NumericPrecision = "NUMERIC_PRECISION";

    internal const string NumericScale = "NUMERIC_SCALE";

    /// <summary>
    /// Normalised to the strings 'YES' and 'NO' by both dialects, so the consumer never has to know
    /// that Oracle reports 'Y' and 'N'.
    /// </summary>
    internal const string IsNullable = "IS_NULLABLE";

    internal const string OrdinalPosition = "ORDINAL_POSITION";

    internal const string ConstraintName = "CONSTRAINT_NAME";

    internal const string ReferencedSchema = "REFERENCED_SCHEMA";

    internal const string ReferencedTable = "REFERENCED_TABLE";

    internal const string ReferencedColumn = "REFERENCED_COLUMN";
}
