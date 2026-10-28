// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Connectors.Sql;

/// <summary>
/// The SQL Connector's Object Types document as written: the model an editor builds, and the shape the
/// Connector's own parser reads, so the two can never disagree about which fields exist.
/// </summary>
/// <remarks>
/// Unvalidated by construction. <see cref="Validate"/> says everything wrong with a document; the Connector
/// reads only documents that pass it, through <see cref="SqlSchemaConfiguration.Parse"/>.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SqlObjectTypesDocument
{
    private static readonly JsonSerializerOptions WritingOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The Object Types, in the order the administrator wrote them.</summary>
    public List<SqlObjectTypeDocument>? ObjectTypes { get; set; }

    /// <summary>
    /// Everything wrong with an Object Types document, in document order, each located as precisely as it
    /// can be; empty when the document is one the Connector accepts. Saving refuses a document with the
    /// first of these.
    /// </summary>
    public static IReadOnlyList<SqlObjectTypesProblem> Validate(string? json) => SqlSchemaConfiguration.Validate(json);

    /// <summary>
    /// Reads a document into the model without validating what it says.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The text is not JSON, or uses a field the document does not have.</exception>
    public static SqlObjectTypesDocument FromJson(string json) =>
        JsonSerializer.Deserialize<SqlObjectTypesDocument>(json, SqlSchemaConfiguration.ParsingOptions) ?? new SqlObjectTypesDocument();

    /// <summary>
    /// Writes the document in its canonical layout: indented two spaces, fields in camel case, unset fields left out.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, WritingOptions);
}
