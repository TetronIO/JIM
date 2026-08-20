// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The parameter names the schema-catalogue queries bind. Schema and object names are values, not
/// identifiers, when they appear in a catalogue filter, so they are always bound rather than
/// interpolated. Callers build them with <see cref="ISqlProvider.CreateParameter"/>.
/// </summary>
internal static class SqlCatalogueParameters
{
    internal const string SchemaName = "catalogueSchemaName";

    internal const string ObjectName = "catalogueObjectName";
}
