// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace JIM.Scim.Service;

/// <summary>
/// An attribute whose values must be unique among the live resources of its type in one Connected System's store: one a
/// schema marks <c>uniqueness</c> <c>server</c> or <c>global</c>, and <c>externalId</c> (D7).
/// </summary>
/// <param name="Name">The name the store keys it by: unqualified for the base schema and the common attributes,
/// URN-qualified for an extension's.</param>
/// <param name="SchemaUrn">The extension the attribute belongs to; null for the base schema and the common attributes.</param>
/// <param name="AttributeName">The attribute's name within its schema.</param>
/// <param name="CaseExact">Whether values compare with regard to case.</param>
public sealed record ScimUniqueAttribute(string Name, string? SchemaUrn, string AttributeName, bool CaseExact)
{
    /// <summary>
    /// A value as it is compared: a string folded to upper case unless the attribute is <c>caseExact</c> (the folding
    /// .NET's ordinal case-insensitive comparison uses, so the store agrees with every other case-insensitive match the
    /// server makes), anything else as its JSON text. Null for a missing or null value.
    /// </summary>
    public string? Normalise(JsonNode? value)
    {
        if (value is not JsonValue scalar)
            return null;

        if (scalar.GetValueKind() == JsonValueKind.String)
        {
            var text = scalar.GetValue<string>();
            return CaseExact ? text : text.ToUpperInvariant();
        }

        return scalar.GetValueKind() == JsonValueKind.Null ? null : scalar.ToJsonString();
    }
}
