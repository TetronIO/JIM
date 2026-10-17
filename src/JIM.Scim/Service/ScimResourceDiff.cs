// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim.Schema;

namespace JIM.Scim.Service;

/// <summary>
/// Which attributes a write changed, by SCIM name (an extension's qualified by its URN), for the journal (D17). Names
/// only, never values.
/// </summary>
public static class ScimResourceDiff
{
    private const string SchemasMember = "schemas";

    /// <summary>
    /// The attributes whose values differ between two forms of a resource, in ordinal order. Pass an empty
    /// <paramref name="before"/> for a creation.
    /// </summary>
    public static IReadOnlyList<string> ChangedAttributes(JsonObject before, JsonObject after, ScimResourceSchema schema)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(schema);

        var changed = new SortedSet<string>(StringComparer.Ordinal);
        Compare(Attributes(before, schema), Attributes(after, schema), changed);
        return changed.ToList();
    }

    private static void Compare(Dictionary<string, JsonNode?> before, Dictionary<string, JsonNode?> after, SortedSet<string> changed)
    {
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(name, out var was);
            after.TryGetValue(name, out var now);
            if (!JsonNode.DeepEquals(was, now))
                changed.Add(name);
        }
    }

    // Each attribute by its journal name: base ones as they are, an extension's under its URN.
    private static Dictionary<string, JsonNode?> Attributes(JsonObject resource, ScimResourceSchema schema)
    {
        var attributes = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in resource.Where(m => !string.Equals(m.Key, SchemasMember, StringComparison.OrdinalIgnoreCase)))
        {
            if (schema.IsExtension(name) && value is JsonObject extension)
            {
                var urn = schema.CanonicalUrn(name);
                foreach (var (memberName, memberValue) in extension)
                    attributes[$"{urn}:{memberName}"] = memberValue;
                continue;
            }

            attributes[name] = value;
        }

        return attributes;
    }
}
