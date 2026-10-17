// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Scim.Messages;
using JIM.Utilities;

namespace JIM.Scim.Schema;

/// <summary>
/// Builds a SCIM resource from JIM attribute values, following the same flattening convention
/// <see cref="ScimAttributeMapper"/> used to build the schema and <see cref="ScimResourceReader"/> reads
/// through.
/// <para>
/// The three have to agree exactly. A value written somewhere the reader would not look is a change JIM
/// records as exported and the next confirming import reports as never applied; a value written in a
/// shape the provider does not recognise is worse, because the provider may accept the request and
/// store nothing.
/// </para>
/// </summary>
public static class ScimResourceWriter
{
    private const string SchemasMember = "schemas";
    private const string ValueSubAttribute = "value";
    private const string TypeSubAttribute = "type";
    private const string PrimarySubAttribute = "primary";

    /// <summary>
    /// Builds a whole resource, as a create (POST) or a replace (PUT) body.
    /// </summary>
    /// <param name="writes">The attribute values to write.</param>
    /// <param name="attributes">The flattened schema attributes, which say where each value belongs.</param>
    /// <param name="baseSchemaUrn">The resource type's own schema URN, always declared in <c>schemas</c>.</param>
    public static ScimResourceWriteResult BuildResource(
        IReadOnlyList<ScimAttributeWrite> writes,
        IReadOnlyList<ScimFlattenedAttribute> attributes,
        string baseSchemaUrn)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSchemaUrn);

        var resource = new JsonObject();
        var schemas = new List<string> { baseSchemaUrn };
        var unknown = new List<string>();

        var lookup = new Dictionary<string, ScimFlattenedAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes)
            lookup[attribute.Name] = attribute;

        foreach (var write in writes)
        {
            if (!lookup.TryGetValue(write.AttributeName, out var attribute))
            {
                unknown.Add(write.AttributeName);
                continue;
            }

            // A provider is entitled to reject an entire request that asserts a read-only attribute, so
            // one flowing here (an id, a meta sub-attribute) is dropped rather than allowed to fail the
            // whole object. It is not reported as unknown: JIM knows the attribute, it just cannot write it.
            if (attribute.Writability == AttributeWritability.ReadOnly)
                continue;

            var values = write.Values.Where(v => v != null).ToList();
            if (values.Count == 0)
                continue;

            // SCIM extension attributes live inside a JSON member named by their URN (RFC 7643 section 3),
            // and a resource has to declare every schema it carries values for.
            var container = resource;
            if (attribute.ExtensionUrn != null)
            {
                container = GetOrCreateObject(resource, attribute.ExtensionUrn);
                if (!schemas.Contains(attribute.ExtensionUrn, StringComparer.OrdinalIgnoreCase))
                    schemas.Add(attribute.ExtensionUrn);
            }

            Write(container, attribute, values);
        }

        resource[SchemasMember] = new JsonArray(schemas.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return new ScimResourceWriteResult { Resource = resource, UnknownAttributes = unknown };
    }

    /// <summary>
    /// Applies changes to a resource the provider already holds, in place.
    /// <para>
    /// This is what makes a whole-resource PUT safe against a provider that does not support PATCH: the
    /// resource is read, JIM's changes are laid onto it, and the result is written back. Building a
    /// fresh resource from JIM's changes alone and PUTting that would clear every attribute the provider
    /// holds that JIM does not manage, because a PUT asserts the entire resource.
    /// </para>
    /// </summary>
    /// <param name="resource">The resource as the provider returned it, mutated in place.</param>
    /// <param name="changes">The changes to lay onto it.</param>
    /// <param name="attributes">The flattened schema attributes.</param>
    public static ScimResourceWriteResult ApplyChanges(
        JsonObject resource,
        IReadOnlyList<ScimAttributeChange> changes,
        IReadOnlyList<ScimFlattenedAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(attributes);

        var unknown = new List<string>();
        var lookup = new Dictionary<string, ScimFlattenedAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes)
            lookup[attribute.Name] = attribute;

        foreach (var change in changes)
        {
            if (!lookup.TryGetValue(change.AttributeName, out var attribute) || attribute.Writability == AttributeWritability.ReadOnly)
            {
                unknown.Add(change.AttributeName);
                continue;
            }

            var container = attribute.ExtensionUrn == null ? resource : GetOrCreateObject(resource, attribute.ExtensionUrn);

            if (string.Equals(change.Operation, ScimPatchOperations.Remove, StringComparison.Ordinal))
                Remove(container, attribute, change.Value);
            else
                Apply(container, attribute, change);
        }

        return new ScimResourceWriteResult { Resource = resource, UnknownAttributes = unknown };
    }

    /// <summary>
    /// Lays one non-removal change onto a resource. An add to a multi-valued attribute appends, where a
    /// replace asserts the whole attribute; collapsing the two would turn every membership addition into
    /// a membership replacement.
    /// </summary>
    private static void Apply(JsonObject container, ScimFlattenedAttribute attribute, ScimAttributeChange change)
    {
        var appending = string.Equals(change.Operation, ScimPatchOperations.Add, StringComparison.Ordinal)
                        && attribute.AttributePlurality == AttributePlurality.MultiValued
                        && attribute.Access is ScimValueAccess.Simple or ScimValueAccess.ComplexReference or ScimValueAccess.ComplexSubAttribute;

        if (!appending)
        {
            Write(container, attribute, [change.Value]);
            return;
        }

        var entries = GetOrCreateArray(container, attribute.SourceAttributeName);

        if (attribute.IsEntryValue)
        {
            // The entry is appended alongside the others, which keep their type, display and primary. An entry already
            // holding the value is left alone: an export not yet confirmed by import is sent again on the next run.
            if (!entries.Any(entry => IsEntryHolding(entry, attribute, change.Value)))
                entries.Add(Entry(change.Value, attribute));
            return;
        }

        entries.Add(attribute.Access == ScimValueAccess.ComplexReference
            ? Reference(change.Value, attribute)
            : ToNode(change.Value, attribute));
    }

    /// <summary>
    /// Takes one value away. Which value matters: removing the attribute where only one entry was meant
    /// to go would take every group membership with it. A removal naming no value is a clear, and takes
    /// every value, as a path-only PATCH remove does (RFC 7644 section 3.5.2.2).
    /// </summary>
    private static void Remove(JsonObject container, ScimFlattenedAttribute attribute, object? value)
    {
        var naming = value != null;

        switch (attribute.Access)
        {
            case ScimValueAccess.Simple when naming && attribute.AttributePlurality == AttributePlurality.MultiValued:
                RemoveMatching(container[attribute.SourceAttributeName] as JsonArray, entry => Matches(entry, value));
                break;

            case ScimValueAccess.ComplexReference when naming && attribute.AttributePlurality == AttributePlurality.MultiValued:
                RemoveMatching(container[attribute.SourceAttributeName] as JsonArray,
                    entry => entry is JsonObject reference && Matches(reference[ValueSubAttribute], value));
                break;

            case ScimValueAccess.ComplexSubAttribute when attribute.IsEntryValue && naming:
                // The value identifies the entry, so the entry goes whole; a provider may also have sent a bare object.
                if (container[attribute.SourceAttributeName] is JsonArray entries)
                    RemoveMatching(entries, entry => IsEntryHolding(entry, attribute, value));
                else if (IsEntryHolding(container[attribute.SourceAttributeName], attribute, value))
                    container.Remove(attribute.SourceAttributeName);
                break;

            case ScimValueAccess.ComplexSubAttribute when !attribute.IsEntryValue:
                (container[attribute.SourceAttributeName] as JsonObject)?.Remove(attribute.SubAttributeName!);
                break;

            case ScimValueAccess.CanonicalSlot:
                RemoveCanonicalSlot(container, attribute);
                break;

            default:
                container.Remove(attribute.SourceAttributeName);
                break;
        }
    }

    private static void RemoveCanonicalSlot(JsonObject container, ScimFlattenedAttribute attribute)
    {
        if (container[attribute.SourceAttributeName] is not JsonArray entries)
            return;

        var entry = FindEntry(entries, attribute);
        if (entry == null)
            return;

        entry.Remove(attribute.SubAttributeName ?? ValueSubAttribute);

        // An entry left holding nothing but the selector that identified it describes nothing, and a
        // provider is entitled to reject it.
        if (entry.All(member => member.Key is TypeSubAttribute or PrimarySubAttribute))
            entries.Remove(entry);
    }

    private static void RemoveMatching(JsonArray? entries, Func<JsonNode?, bool> predicate)
    {
        if (entries == null)
            return;

        foreach (var entry in entries.Where(predicate).ToList())
            entries.Remove(entry);
    }

    private static bool IsEntryHolding(JsonNode? entry, ScimFlattenedAttribute attribute, object? value)
    {
        return entry is JsonObject entryObject && Matches(entryObject[attribute.SubAttributeName!], value);
    }

    /// <summary>
    /// Whether a value the provider holds is the value JIM names. A date compares as an instant at the microsecond JIM
    /// holds (#2153), since a provider may spell one instant with a fraction of zeros or an offset; everything else by
    /// its text.
    /// </summary>
    private static bool Matches(JsonNode? node, object? value)
    {
        if (node == null)
            return false;

        if (value is DateTime date)
        {
            return DateTimeOffset.TryParse(node.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var held) &&
                   DateTimeAttributeValue.Key(held.UtcDateTime) == DateTimeAttributeValue.Key(date);
        }

        return string.Equals(node.ToString(), ScimValueFormatter.ToText(value), StringComparison.Ordinal);
    }

    private static void Write(JsonObject container, ScimFlattenedAttribute attribute, List<object?> values)
    {
        var multiValued = attribute.AttributePlurality == AttributePlurality.MultiValued;

        switch (attribute.Access)
        {
            case ScimValueAccess.Simple:
                container[attribute.SourceAttributeName] = multiValued
                    ? Array(values, attribute)
                    : ToNode(values[0], attribute);
                break;

            case ScimValueAccess.ComplexSubAttribute when attribute.IsEntryValue:
                // Each value is an entry of its own (#2090). Writing the first into one bare object lost the rest.
                container[attribute.SourceAttributeName] = new JsonArray(values.Select(v => (JsonNode?)Entry(v, attribute)).ToArray());
                break;

            case ScimValueAccess.ComplexSubAttribute:
                // Several flattened attributes share one parent object (name.givenName and
                // name.familyName are both members of name), so it is created once and added to.
                GetOrCreateObject(container, attribute.SourceAttributeName)[attribute.SubAttributeName!] =
                    ToNode(values[0], attribute);
                break;

            case ScimValueAccess.CanonicalSlot:
                WriteCanonicalSlot(container, attribute, values[0]);
                break;

            case ScimValueAccess.ComplexReference:
                container[attribute.SourceAttributeName] = multiValued
                    ? new JsonArray(values.Select(v => (JsonNode?)Reference(v, attribute)).ToArray())
                    : Reference(values[0], attribute);
                break;
        }
    }

    /// <summary>
    /// Writes one canonically-typed slot, for example <c>emails.work</c>, into the entry it belongs to.
    /// The entry carries the selector that identifies it (<c>type</c>, or <c>primary</c>), because
    /// without it neither the provider nor the next import can tell one entry from another.
    /// </summary>
    private static void WriteCanonicalSlot(JsonObject container, ScimFlattenedAttribute attribute, object? value)
    {
        var entries = GetOrCreateArray(container, attribute.SourceAttributeName);
        var entry = FindEntry(entries, attribute);

        if (entry == null)
        {
            entry = new JsonObject();
            entries.Add(entry);
        }

        // Slots cut per sub-attribute (an address has no single value) share one entry, so the
        // sub-attribute is set and the selector stamped rather than the entry being rebuilt.
        entry[attribute.SubAttributeName ?? ValueSubAttribute] = ToNode(value, attribute);

        if (attribute.SelectsPrimary)
            entry[PrimarySubAttribute] = JsonValue.Create(true);
        else if (attribute.CanonicalType != null)
            entry[TypeSubAttribute] = JsonValue.Create(attribute.CanonicalType);
    }

    private static JsonObject? FindEntry(JsonArray entries, ScimFlattenedAttribute attribute)
    {
        return entries
            .OfType<JsonObject>()
            .FirstOrDefault(entry => attribute.SelectsPrimary
                ? entry[PrimarySubAttribute]?.GetValue<bool>() == true
                : string.Equals(entry[TypeSubAttribute]?.GetValue<string>(), attribute.CanonicalType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A reference is written as a complex value carrying the referenced resource's id, which is the
    /// form RFC 7643 section 4 defines and the reader looks for.
    /// </summary>
    private static JsonObject Reference(object? value, ScimFlattenedAttribute attribute)
    {
        return new JsonObject { [ValueSubAttribute] = ToNode(value, attribute) };
    }

    /// <summary>
    /// One entry of a multi-valued complex attribute, carrying only the sub-attribute that identifies it.
    /// </summary>
    private static JsonObject Entry(object? value, ScimFlattenedAttribute attribute)
    {
        return new JsonObject { [attribute.SubAttributeName!] = ToNode(value, attribute) };
    }

    private static JsonArray Array(List<object?> values, ScimFlattenedAttribute attribute)
    {
        return new JsonArray(values.Select(v => ToNode(v, attribute)).ToArray());
    }

    /// <summary>
    /// The complex value to write a sub-attribute into. A provider that sent an array where the schema says one value
    /// keeps it: its first entry is written into, and the rest are left alone rather than discarded.
    /// </summary>
    private static JsonObject GetOrCreateObject(JsonObject container, string name)
    {
        switch (container[name])
        {
            case JsonObject existing:
                return existing;

            case JsonArray entries:
                var first = entries.OfType<JsonObject>().FirstOrDefault();
                if (first != null)
                    return first;

                var entry = new JsonObject();
                entries.Add(entry);
                return entry;
        }

        var created = new JsonObject();
        container[name] = created;
        return created;
    }

    /// <summary>
    /// The array of a multi-valued attribute. A provider that sent a single bare value where the schema says
    /// multi-valued (the reader accepts one) has it kept as the first entry, never replaced by the value being added.
    /// </summary>
    private static JsonArray GetOrCreateArray(JsonObject container, string name)
    {
        if (container[name] is JsonArray existing)
            return existing;

        var bare = container[name];
        container.Remove(name);

        var created = bare == null ? new JsonArray() : new JsonArray(bare);
        container[name] = created;
        return created;
    }

    private static JsonNode? ToNode(object? value, ScimFlattenedAttribute attribute)
    {
        return ScimValueFormatter.ToNode(value, attribute.Type);
    }
}
