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
        var writeOnly = new List<string>();

        var lookup = new Dictionary<string, ScimFlattenedAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes)
            lookup[attribute.Name] = attribute;

        foreach (var change in changes)
        {
            if (lookup.TryGetValue(change.AttributeName, out var writeOnlyAttribute) && writeOnlyAttribute.IsWriteOnly)
            {
                // Never sent, a removal included: a password reaches the provider only through Password
                // Synchronisation (#2091).
                writeOnly.Add(change.AttributeName);
                continue;
            }

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

        return new ScimPatchBuildResult { Operations = operations, UnknownAttributes = unknown, WriteOnlyAttributes = writeOnly };
    }

    private static ScimPatchOperation BuildOperation(ScimAttributeChange change, ScimFlattenedAttribute attribute)
    {
        var removing = string.Equals(change.Operation, ScimPatchOperations.Remove, StringComparison.Ordinal);

        // A removal from a multi-valued attribute has to name the value going away: RFC 7644 section 3.5.2.2 reads
        // "remove members" or "remove badges" as taking every value, where "remove badges[value eq "gold"]" takes the
        // one. That holds for a reference and for a simple attribute alike, since both compare by value.
        // An entry of a multi-valued complex attribute without canonical types (roles.value) is addressed as a reference
        // is: its own path, roles.value, would reach every entry's value at once (#2090).
        if (attribute.Access == ScimValueAccess.ComplexReference || attribute.IsEntryValue)
            return EntryOperation(change, attribute, removing);

        if (removing && attribute.Access == ScimValueAccess.Simple)
            return new ScimPatchOperation { Op = ScimPatchOperations.Remove, Path = RemovalPath(change, attribute) };

        return new ScimPatchOperation
        {
            Op = change.Operation,
            Path = Qualify(attribute, attribute.ScimPath),
            // RFC 7644 section 3.5.2.2: a removal is expressed by the path alone. A canonically-typed slot or a
            // sub-attribute of a single-valued complex attribute is one value at its own path.
            Value = removing ? null : ScimValueFormatter.ToNode(change.Value, attribute.Type)
        };
    }

    /// <summary>
    /// An operation on one entry of a complex attribute identified by its <c>value</c>: a reference, or an entry of a
    /// multi-valued complex attribute without canonical types. An add appends the entry to the attribute, where an add
    /// at <c>roles.value</c> would replace every entry's value (RFC 7644 section 3.5.2.1).
    /// </summary>
    private static ScimPatchOperation EntryOperation(ScimAttributeChange change, ScimFlattenedAttribute attribute, bool removing)
    {
        if (removing)
            return new ScimPatchOperation { Op = ScimPatchOperations.Remove, Path = RemovalPath(change, attribute) };

        var value = ScimValueFormatter.ToNode(change.Value, attribute.Type);
        return new ScimPatchOperation
        {
            Op = change.Operation,
            Path = Qualify(attribute, attribute.SourceAttributeName),
            Value = attribute.AttributePlurality == AttributePlurality.MultiValued
                ? new JsonArray(new JsonObject { [ValueSubAttribute] = value })
                : new JsonObject { [ValueSubAttribute] = value }
        };
    }

    /// <summary>
    /// The path of a removal from an attribute whose values are compared by <c>value</c>: a simple attribute's values
    /// themselves, or a reference's <c>value</c> sub-attribute. One value of a multi-valued attribute is selected by a
    /// value filter. A removal naming no value is a clear, so its path names no value either: a filter on an empty
    /// value would match nothing and remove nothing.
    /// </summary>
    private static string RemovalPath(ScimAttributeChange change, ScimFlattenedAttribute attribute)
    {
        if (attribute.AttributePlurality != AttributePlurality.MultiValued || change.Value == null)
            return Qualify(attribute, attribute.SourceAttributeName);

        return Qualify(attribute, $"{attribute.SourceAttributeName}[{ValueSubAttribute} eq {ScimFilterLiteral.For(change.Value, attribute.Type)}]");
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
}
