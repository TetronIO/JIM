// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Scim.Messages;

namespace JIM.Scim.Schema;

/// <summary>
/// Turns JIM attribute changes into SCIM PATCH operations (RFC 7644 section 3.5.2).
/// <para>
/// PATCH is preferred over a whole-resource PUT because it says only what changed: a PUT asserts the
/// entire resource, so any attribute JIM does not manage would be cleared by the act of updating one it
/// does.
/// </para>
/// </summary>
public static class ScimPatchBuilder
{
    private const string ValueSubAttribute = "value";

    /// <summary>
    /// Builds the operations for one resource's changes.
    /// </summary>
    /// <param name="changes">The changes to apply, in the order JIM recorded them.</param>
    /// <param name="attributes">The flattened schema attributes, which say how each value is addressed.</param>
    public static ScimPatchBuildResult Build(
        IReadOnlyList<ScimAttributeChange> changes,
        IReadOnlyList<ScimFlattenedAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(attributes);

        var operations = new List<ScimPatchOperation>();
        var unknown = new List<string>();

        var lookup = new Dictionary<string, ScimFlattenedAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes)
            lookup[attribute.Name] = attribute;

        foreach (var change in changes)
        {
            if (!lookup.TryGetValue(change.AttributeName, out var attribute) || attribute.Writability == AttributeWritability.ReadOnly)
            {
                // Both cases are the same problem from the administrator's side: a change JIM holds that
                // this provider will not take. Reported, so the Pending Export fails rather than being
                // recorded as applied.
                unknown.Add(change.AttributeName);
                continue;
            }

            operations.Add(BuildOperation(change, attribute));
        }

        return new ScimPatchBuildResult { Operations = operations, UnknownAttributes = unknown };
    }

    private static ScimPatchOperation BuildOperation(ScimAttributeChange change, ScimFlattenedAttribute attribute)
    {
        var removing = string.Equals(change.Operation, ScimPatchOperations.Remove, StringComparison.Ordinal);

        // A multi-valued reference is the one shape whose path has to name the value going away:
        // "remove members" would take every member, where "remove members[value eq id]" takes one.
        if (attribute.Access == ScimValueAccess.ComplexReference)
            return ReferenceOperation(change, attribute, removing);

        return new ScimPatchOperation
        {
            Op = change.Operation,
            Path = Qualify(attribute, attribute.ScimPath),
            // RFC 7644 section 3.5.2.2: a removal is expressed by the path alone.
            Value = removing ? null : ScimValueFormatter.ToNode(change.Value, attribute.Type)
        };
    }

    private static ScimPatchOperation ReferenceOperation(ScimAttributeChange change, ScimFlattenedAttribute attribute, bool removing)
    {
        var multiValued = attribute.AttributePlurality == AttributePlurality.MultiValued;
        var value = ScimValueFormatter.ToNode(change.Value, attribute.Type);

        if (removing)
        {
            return new ScimPatchOperation
            {
                Op = ScimPatchOperations.Remove,
                Path = multiValued
                    ? Qualify(attribute, $"{attribute.SourceAttributeName}[{ValueSubAttribute} eq \"{Escape(change.Value)}\"]")
                    : Qualify(attribute, attribute.SourceAttributeName)
            };
        }

        return new ScimPatchOperation
        {
            Op = change.Operation,
            Path = Qualify(attribute, attribute.SourceAttributeName),
            Value = multiValued
                ? new JsonArray(new JsonObject { [ValueSubAttribute] = value })
                : new JsonObject { [ValueSubAttribute] = value }
        };
    }

    /// <summary>
    /// Prefixes an extension attribute's path with its schema URN, which is how RFC 7644 addresses one.
    /// A path already carrying the URN (as <see cref="ScimFlattenedAttribute.ScimPath"/> does) is left
    /// alone.
    /// </summary>
    private static string Qualify(ScimFlattenedAttribute attribute, string path)
    {
        if (attribute.ExtensionUrn == null || path.StartsWith(attribute.ExtensionUrn, StringComparison.OrdinalIgnoreCase))
            return path;

        return $"{attribute.ExtensionUrn}:{path}";
    }

    /// <summary>
    /// Escapes a value going into a path filter. A quote in an identifier would otherwise close the
    /// filter's string early and change what the operation targets.
    /// </summary>
    private static string Escape(object? value)
    {
        return (value?.ToString() ?? string.Empty).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
