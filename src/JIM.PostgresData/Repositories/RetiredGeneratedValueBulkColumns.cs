// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column list of every raw-SQL write to the retired values register
/// (Unique Value Generation, #242, Phase 6): the <c>INSERT ... SELECT</c> statements in
/// <see cref="RetiredGeneratedValueSql"/> that retire the values held by deleted Metaverse Objects, deleted
/// Connected System Objects and superseded assignments. Every writer MUST project its <c>SELECT</c> list in exactly
/// this order. <c>BulkInsertColumnCompletenessTests</c> asserts the list matches the EF model's mapped columns, so
/// a migration that adds a column fails the unit pass until every writer is extended.
/// <para>
/// The database trigger that retires a removed flow's values (<c>RetireGeneratedValuesOnGenerationDelete</c>, in
/// migration <c>AddRetiredGeneratedValues</c>) carries the same list as static SQL, because a trigger cannot read a
/// C# constant; <c>RetiredGeneratedValueRegisterDatabaseTests</c> asserts its definition names every column here.
/// </para>
/// </summary>
internal static class RetiredGeneratedValueBulkColumns
{
    /// <summary>
    /// Insert columns for the RetiredGeneratedValues table. Id is omitted: it is an identity column the database
    /// assigns. There is no update list: a register entry is written once and never changed.
    /// </summary>
    internal static readonly string[] RetiredGeneratedValues =
    [
        "MetaverseAttributeId", "ConnectedSystemObjectTypeAttributeId", "Value", "NormalisedValue", "RetiredAt",
        "Reason", "FromObjectDisplayName", "FromObjectId", "ActivityId"
    ];
}
