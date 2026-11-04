// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JIM.PostgresData;

/// <summary>
/// Stores a list of facts (<see cref="ConnectorCapability"/>) as JSON text in one column: what a Connector's settings
/// check learnt (<see cref="ConnectedSystem.SettingsCheckFacts"/>, #2021), read and written only as a whole.
/// </summary>
internal static class ConnectorCapabilityJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Converts the list to and from its JSON text. EF Core never calls a converter for null, so a null list is a null
    /// column.
    /// </summary>
    public static readonly ValueConverter<List<ConnectorCapability>?, string?> Converter = new(
        list => Serialise(list),
        json => Deserialise(json));

    /// <summary>
    /// Compares two lists by their content, so a list changed in place is seen as changed and two lists holding the
    /// same values are not.
    /// </summary>
    public static readonly ValueComparer<List<ConnectorCapability>?> Comparer = new(
        (left, right) => Serialise(left) == Serialise(right),
        list => Serialise(list) == null ? 0 : Serialise(list)!.GetHashCode(),
        list => Deserialise(Serialise(list)));

    private static string? Serialise(List<ConnectorCapability>? list) =>
        list == null ? null : JsonSerializer.Serialize(list, Options);

    private static List<ConnectorCapability>? Deserialise(string? json) =>
        json == null ? null : JsonSerializer.Deserialize<List<ConnectorCapability>>(json, Options);
}
