// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Activities;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JIM.PostgresData;

/// <summary>
/// How an import Activity's per-Partition watermarks (<see cref="Activity.ImportPartitionWatermarksFrom"/> and
/// <see cref="Activity.ImportPartitionWatermarksTo"/>, #2715) are held in their columns: a JSON array of
/// <c>{ partitionId, partitionName, value }</c>.
/// </summary>
internal static class ImportPartitionWatermarkJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Converts the list to and from its JSON text. EF Core never calls a converter for null, so a null list is a null
    /// column.
    /// </summary>
    public static readonly ValueConverter<List<ImportPartitionWatermark>?, string?> Converter = new(
        list => Serialise(list),
        json => Deserialise(json));

    /// <summary>
    /// Compares two lists by their content, so a list changed in place is seen as changed and two lists holding the
    /// same values are not.
    /// </summary>
    public static readonly ValueComparer<List<ImportPartitionWatermark>?> Comparer = new(
        (left, right) => Serialise(left) == Serialise(right),
        list => Serialise(list) == null ? 0 : Serialise(list)!.GetHashCode(),
        list => Deserialise(Serialise(list)));

    private static string? Serialise(List<ImportPartitionWatermark>? list) =>
        list == null ? null : JsonSerializer.Serialize(list, Options);

    private static List<ImportPartitionWatermark>? Deserialise(string? json) =>
        json == null ? null : JsonSerializer.Deserialize<List<ImportPartitionWatermark>>(json, Options);
}
