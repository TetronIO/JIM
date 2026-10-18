// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Scim.Discovery;
using JIM.Scim.Filtering;
using JIM.Scim.Messages;

namespace JIM.Scim.Schema;

/// <summary>
/// Applies a SCIM PATCH request (RFC 7644 section 3.5.2) to a resource the Service Provider holds: the server side of
/// what <see cref="ScimPatchBuilder"/> sends. Every form the builder emits lands exactly as the client meant it,
/// including its non-strict ones (a value-less <c>replace</c> to clear, a filtered <c>replace</c> on an entry that does
/// not exist yet, a scalar <c>add</c> to a multi-valued attribute, an unquoted <c>primary eq true</c>); the forms other
/// identity providers send are applied by the RFC, with the interoperability leniency the design names.
/// <para>
/// A request is atomic: every operation applies to a copy of the resource, and the first that cannot be applied fails
/// the request with nothing applied. Errors name attributes and never repeat a value or a filter's literal. A request
/// that names a write-only attribute (<c>password</c>) in any operation is refused before anything is applied (D5).
/// </para>
/// </summary>
public static class ScimPatchApplier
{
    private const string TypeSubAttribute = "type";
    private const string PrimarySubAttribute = "primary";
    private const string SchemasMember = "schemas";
    private const string ValueSubAttribute = "value";
    private const string ReferenceSubAttribute = "$ref";

    private static readonly string[] Operations = [ScimPatchOperations.Add, ScimPatchOperations.Replace, ScimPatchOperations.Remove];

    /// <summary>
    /// Applies <paramref name="operations"/> in order to a copy of <paramref name="resource"/>, which is never changed.
    /// </summary>
    /// <param name="resource">The resource as the Service Provider holds it.</param>
    /// <param name="operations">The request's operations.</param>
    /// <param name="schema">The resource type's schemas, which say how each attribute is addressed and what it holds.</param>
    public static ScimPatchResult Apply(JsonObject resource, IReadOnlyList<ScimPatchOperation> operations, ScimResourceSchema schema)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(schema);

        var patch = new Patch(schema);
        var working = (JsonObject)resource.DeepClone();

        try
        {
            foreach (var operation in operations)
                patch.Apply(working, operation);
        }
        catch (ScimPatchRefusal refusal)
        {
            return new ScimPatchResult { Error = refusal.ToError(), Coercions = patch.Coercions };
        }

        return new ScimPatchResult { Resource = working, Coercions = patch.Coercions, IgnoredAttributes = patch.Ignored };
    }

    /// <summary>
    /// One request's application, holding what it needs to report.
    /// </summary>
    private sealed class Patch(ScimResourceSchema schema)
    {
        public List<string> Coercions { get; } = [];

        /// <summary>
        /// The attributes the request named that the resource type does not declare, by full name, each once.
        /// </summary>
        public List<string> Ignored { get; } = [];

        private void Ignore(string name)
        {
            if (!Ignored.Contains(name, StringComparer.OrdinalIgnoreCase))
                Ignored.Add(name);
        }

        public void Apply(JsonObject resource, ScimPatchOperation operation)
        {
            var op = Normalise(operation.Op);

            if (string.IsNullOrWhiteSpace(operation.Path))
            {
                // RFC 7644 section 3.5.2.2: a remove names its target, there being no value to name it by.
                if (op == ScimPatchOperations.Remove)
                    throw new ScimPatchRefusal(ScimErrorTypes.NoTarget, "A remove operation needs a path.");

                var value = operation.Value;
                if (value is JsonArray { Count: 1 } single && single[0] is JsonObject)
                {
                    Coercions.Add("A single-element array was read as the object value of an operation with no path.");
                    value = single[0];
                }

                if (value is not JsonObject members)
                    throw new ScimPatchRefusal(ScimErrorTypes.InvalidValue, "An operation with no path needs an object value naming the attributes to change.");

                ApplyMembers(resource, op, members);
                return;
            }

            ScimPatchPath path;
            try
            {
                path = ScimFilterParser.ParsePath(operation.Path);
            }
            catch (ScimFilterException)
            {
                // The parser's message gives a position, but the path may carry a filter literal, so none of it is repeated.
                throw new ScimPatchRefusal(ScimErrorTypes.InvalidPath, "The path is not a valid SCIM attribute path.");
            }

            ApplyPath(resource, op, path, operation.Value);
        }

        private string Normalise(string? op)
        {
            foreach (var known in Operations)
            {
                if (string.Equals(op, known, StringComparison.Ordinal))
                    return known;

                if (string.Equals(op, known, StringComparison.OrdinalIgnoreCase))
                {
                    Coercions.Add($"The op was read as '{known}' without regard to case.");
                    return known;
                }
            }

            throw new ScimPatchRefusal(ScimErrorTypes.InvalidSyntax, "Each operation's op must be add, replace or remove.");
        }

        #region no path
        /// <summary>
        /// An operation with no path: each member of the value names an attribute (RFC 7644 sections 3.5.2.1 and
        /// 3.5.2.3), an extension's attributes inside a member named by its URN.
        /// </summary>
        private void ApplyMembers(JsonObject resource, string op, JsonObject members)
        {
            // The schemas a resource carries are the Service Provider's to state.
            foreach (var (name, value) in members.Where(member => !string.Equals(member.Key, SchemasMember, StringComparison.OrdinalIgnoreCase)))
            {
                if (schema.Declares(name))
                {
                    if (value is not JsonObject extension)
                        throw new ScimPatchRefusal(ScimErrorTypes.InvalidValue, $"The schema '{schema.CanonicalUrn(name)}' takes an object of its attributes.");

                    var urn = schema.IsExtension(name) ? schema.CanonicalUrn(name) : null;
                    foreach (var (memberName, memberValue) in extension)
                        ApplyNamed(resource, op, urn, memberName, memberValue);
                    continue;
                }

                if (schema.TrySplitQualifiedName(name, out var schemaUrn, out var attributeName))
                {
                    ApplyNamed(resource, op, schema.IsExtension(schemaUrn) ? schemaUrn : null, attributeName, value);
                    continue;
                }

                if (name.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
                {
                    Coercions.Add("A member of a schema this resource type does not declare was ignored.");
                    if (value is JsonObject undeclared && undeclared.Count > 0)
                    {
                        foreach (var memberName in undeclared.Select(member => member.Key))
                            Ignore($"{name}:{memberName}");
                    }
                    else
                    {
                        Ignore(name);
                    }

                    continue;
                }

                ApplyNamed(resource, op, null, name, value);
            }
        }

        /// <summary>
        /// One attribute named by a member of an operation's value. An attribute the resource type does not define, or a
        /// read-only one, is ignored rather than failing the request: identity providers routinely send both (D6).
        /// </summary>
        private void ApplyNamed(JsonObject resource, string op, string? schemaUrn, string name, JsonNode? value)
        {
            var attribute = schema.Find(schemaUrn, name);
            if (attribute == null)
            {
                Coercions.Add($"The attribute '{name}' is not defined for this resource type and was ignored.");
                Ignore(schemaUrn == null ? name : $"{schemaUrn}:{name}");
                return;
            }

            Guard(attribute);
            if (ScimResourceSchema.IsReadOnly(attribute))
            {
                Coercions.Add($"The read-only attribute '{attribute.Name}' was ignored.");
                return;
            }

            var container = Container(resource, schemaUrn, create: true)!;
            ApplyAttribute(container, attribute, op, value, attribute.Name!);
            RemoveIfEmpty(resource, schemaUrn);
        }
        #endregion

        #region path
        private void ApplyPath(JsonObject resource, string op, ScimPatchPath path, JsonNode? value)
        {
            var schemaUrn = path.Attribute.SchemaUrn;
            if (schemaUrn != null && !schema.Declares(schemaUrn))
            {
                Coercions.Add("An operation on a schema this resource type does not declare was ignored.");
                var ignoredSub = path.Attribute.SubAttributeName == null ? "" : $".{path.Attribute.SubAttributeName}";
                Ignore($"{schemaUrn}:{path.Attribute.AttributeName}{ignoredSub}");
                return;
            }

            if (schemaUrn != null)
                schemaUrn = schema.IsExtension(schemaUrn) ? schema.CanonicalUrn(schemaUrn) : null;

            var attribute = schema.Find(schemaUrn, path.Attribute.AttributeName)
                            ?? throw new ScimPatchRefusal(ScimErrorTypes.InvalidPath, $"The attribute '{path.Attribute.AttributeName}' is not defined for this resource type.");
            Guard(attribute);

            var sub = SubAttribute(attribute, path.Attribute.SubAttributeName);
            var filterSub = SubAttribute(attribute, path.SubAttributeName);

            if (ScimResourceSchema.IsReadOnly(attribute) || (sub != null && ScimResourceSchema.IsReadOnly(sub)) ||
                (filterSub != null && ScimResourceSchema.IsReadOnly(filterSub)))
            {
                Coercions.Add($"An operation on the read-only attribute '{attribute.Name}' was ignored.");
                return;
            }

            var container = Container(resource, schemaUrn, create: op != ScimPatchOperations.Remove);
            if (container == null)
                return;

            if (path.ValueFilter == null)
            {
                if (sub == null)
                    ApplyAttribute(container, attribute, op, value, attribute.Name!);
                else
                    ApplySubAttribute(container, attribute, sub, op, value);
            }
            else
            {
                ApplyFiltered(container, attribute, path.ValueFilter, filterSub, op, value);
            }

            RemoveIfEmpty(resource, schemaUrn);
        }

        private ScimSchemaAttribute? SubAttribute(ScimSchemaAttribute attribute, string? name)
        {
            if (name == null)
                return null;

            if (attribute.SubAttributes.Count == 0)
                throw new ScimPatchRefusal(ScimErrorTypes.InvalidPath, $"The attribute '{attribute.Name}' has no sub-attributes.");

            var sub = ScimResourceSchema.FindSubAttribute(attribute, name)
                      ?? throw new ScimPatchRefusal(ScimErrorTypes.InvalidPath, $"The attribute '{attribute.Name}' has no sub-attribute '{name}'.");
            Guard(sub, attribute);
            return sub;
        }

        /// <summary>
        /// A sub-attribute addressed without a filter (<c>name.givenName</c>): the one complex value's, or every entry's
        /// of a multi-valued attribute (RFC 7644 section 3.5.2.3).
        /// </summary>
        private void ApplySubAttribute(JsonObject container, ScimSchemaAttribute attribute, ScimSchemaAttribute sub, string op, JsonNode? value)
        {
            var label = $"{attribute.Name}.{sub.Name}";

            if (attribute.MultiValued)
            {
                foreach (var entry in ExistingArray(container, attribute)?.OfType<JsonObject>().ToList() ?? [])
                    ApplyAttribute(entry, sub, op, value, label);
                return;
            }

            var key = ScimEntryFilter.Key(container, attribute.Name!);
            var parent = key == null ? null : container[key] switch
            {
                JsonObject single => single,
                // A provider-shaped array where the schema says one value: its first entry is the value.
                JsonArray entries => entries.OfType<JsonObject>().FirstOrDefault(),
                _ => null
            };

            if (parent == null)
            {
                if (op == ScimPatchOperations.Remove)
                    return;

                if (key != null)
                    container.Remove(key);
                parent = new JsonObject();
                container[attribute.Name!] = parent;
            }

            ApplyAttribute(parent, sub, op, value, label);
            if (parent.Count == 0)
                container.Remove(ScimEntryFilter.Key(container, attribute.Name!)!);
        }

        /// <summary>
        /// The entries of a multi-valued attribute a value filter selects (<c>emails[type eq "work"]</c>), or one
        /// sub-attribute of each (<c>emails[type eq "work"].value</c>).
        /// </summary>
        private void ApplyFiltered(JsonObject container, ScimSchemaAttribute attribute, ScimFilter filter, ScimSchemaAttribute? filterSub, string op, JsonNode? value)
        {
            if (!attribute.MultiValued)
                throw new ScimPatchRefusal(ScimErrorTypes.InvalidPath, $"The attribute '{attribute.Name}' is single-valued, so a value filter cannot select from it.");

            var entries = ExistingArray(container, attribute);
            var matches = entries?.Where(entry => ScimEntryFilter.Matches(entry, filter, attribute)).ToList() ?? [];

            if (op == ScimPatchOperations.Remove)
            {
                // A value no longer held is already removed: a client re-sending a removal its last export did not see
                // confirmed is told it succeeded, which it has.
                foreach (var match in matches)
                {
                    if (filterSub == null)
                    {
                        entries!.Remove(match);
                        continue;
                    }

                    if (match is not JsonObject entry)
                        continue;

                    var key = ScimEntryFilter.Key(entry, filterSub.Name!);
                    if (key != null)
                        entry.Remove(key);

                    // An entry left holding nothing but the selectors that identified it describes nothing.
                    if (entry.All(member => IsSelector(member.Key)))
                        entries!.Remove(entry);
                }

                RemoveIfEmptyArray(container, attribute);
                return;
            }

            if (matches.Count == 0)
            {
                // The builder's replace of a canonical slot (emails[type eq "work"].value) on an entry not there yet: the
                // filter describes the entry, so it is created. A filter that does not describe one leaves no target.
                var described = attribute.SubAttributes.Count == 0 ? null : ScimEntryFilter.DescribedEntry(filter, attribute);
                if (described == null)
                    throw new ScimPatchRefusal(ScimErrorTypes.NoTarget, $"No value of '{attribute.Name}' matches the filter.");

                entries ??= CreateArray(container, attribute);
                entries.Add(described);
                matches = [described];
            }

            foreach (var match in matches)
            {
                if (attribute.SubAttributes.Count == 0)
                {
                    // A multi-valued simple attribute: the selected values are replaced by the one given.
                    var index = entries!.IndexOf(match);
                    entries[index] = Coerce(Single(value, attribute.Name!) ?? throw Invalid(attribute.Name!), attribute, attribute.Name!);
                    continue;
                }

                if (match is not JsonObject entry)
                    continue;

                if (filterSub != null)
                {
                    ApplyAttribute(entry, filterSub, ScimPatchOperations.Replace, value, $"{attribute.Name}.{filterSub.Name}");
                    continue;
                }

                if (Single(value, attribute.Name!) is not JsonObject members)
                    throw new ScimPatchRefusal(ScimErrorTypes.InvalidValue, $"The entries of '{attribute.Name}' take an object value.");

                Merge(entry, attribute, members);
            }

            RemoveIfEmptyArray(container, attribute);
        }
        #endregion

        #region attributes
        /// <summary>
        /// A whole attribute: set, cleared, merged or appended to by the operation.
        /// </summary>
        private void ApplyAttribute(JsonObject container, ScimSchemaAttribute attribute, string op, JsonNode? value, string label)
        {
            var key = ScimEntryFilter.Key(container, attribute.Name!);

            if (op == ScimPatchOperations.Remove)
            {
                if (value != null && attribute.MultiValued && key != null)
                {
                    RemoveListed(container, attribute, value);
                    return;
                }

                if (key != null)
                    container.Remove(key);
                return;
            }

            if (IsAbsent(value))
            {
                // A value-less replace clears the attribute: the builder's form for a change to no value.
                if (!attribute.MultiValued || op == ScimPatchOperations.Replace)
                {
                    if (key != null)
                        container.Remove(key);
                }
                return;
            }

            if (!attribute.MultiValued)
            {
                var single = Single(value, label);
                if (IsAbsent(single))
                {
                    if (key != null)
                        container.Remove(key);
                    return;
                }

                if (IsComplex(attribute))
                {
                    if (single is not JsonObject members)
                        throw new ScimPatchRefusal(ScimErrorTypes.InvalidValue, $"The attribute '{label}' is complex and takes an object value.");

                    var target = key == null ? null : container[key] as JsonObject;
                    if (target == null)
                    {
                        if (key != null)
                            container.Remove(key);
                        target = new JsonObject();
                        container[attribute.Name!] = target;
                    }

                    // RFC 7644 section 3.5.2.3: sub-attributes the value does not name are left as they were.
                    Merge(target, attribute, members);
                    if (target.Count == 0)
                        container.Remove(ScimEntryFilter.Key(container, attribute.Name!)!);
                    return;
                }

                var coerced = Coerce(single!, attribute, label);
                if (key != null)
                    container.Remove(key);
                container[attribute.Name!] = coerced;
                return;
            }

            // A multi-valued attribute takes an array; the builder sends a scalar add or replace for one value.
            var items = (value is JsonArray array ? array.ToList() : [value])
                .Where(item => !IsAbsent(item))
                .Select(item => Element(item!, attribute, label))
                .ToList();

            if (op == ScimPatchOperations.Replace)
            {
                if (key != null)
                    container.Remove(key);
                if (items.Count > 0)
                    container[attribute.Name!] = new JsonArray(items.ToArray());
                return;
            }

            var held = ExistingArray(container, attribute) ?? CreateArray(container, attribute);

            // RFC 7644 section 3.5.2.1: a value the attribute already holds is not added again.
            foreach (var item in items.Where(item => !held.Any(existing => JsonNode.DeepEquals(existing, item))))
                held.Add(item);

            RemoveIfEmptyArray(container, attribute);
        }

        /// <summary>
        /// A remove naming a multi-valued attribute with a value: only the values listed go. RFC 7644 reads it as removing
        /// every value; some identity providers send it meaning the listed ones, and taking it literally would empty a
        /// group's whole membership.
        /// </summary>
        private void RemoveListed(JsonObject container, ScimSchemaAttribute attribute, JsonNode value)
        {
            Coercions.Add($"A remove of '{attribute.Name}' carrying values removed only those values.");

            var listed = value is JsonArray array ? array.ToList() : [value];
            var entries = ExistingArray(container, attribute);
            if (entries == null)
                return;

            foreach (var entry in entries.Where(entry => listed.Any(item => Describes(item, entry, attribute))).ToList())
                entries.Remove(entry);

            RemoveIfEmptyArray(container, attribute);
        }

        /// <summary>
        /// Whether a listed value names a held entry: for a complex entry, every member it gives is the entry's. An entry
        /// of a reference attribute (one with a <c>$ref</c>, such as <c>members</c>) is named by its <c>value</c> alone:
        /// identity providers echo <c>display</c> and <c>$ref</c> back in a remove, and the held entry need not carry
        /// them (the server keeps only the id), so matching every member would leave the member in place.
        /// </summary>
        private static bool Describes(JsonNode? item, JsonNode? entry, ScimSchemaAttribute attribute)
        {
            if (item is JsonObject listed && entry is JsonObject heldReference &&
                ScimResourceSchema.FindSubAttribute(attribute, ReferenceSubAttribute) != null &&
                ScimEntryFilter.Member(listed, ValueSubAttribute) is { } listedValue)
            {
                var heldValue = ScimEntryFilter.Member(heldReference, ValueSubAttribute);
                var valueSub = ScimResourceSchema.FindSubAttribute(attribute, ValueSubAttribute);
                return heldValue != null && SameValue(heldValue, listedValue, valueSub?.CaseExact ?? false);
            }

            if (item is JsonObject wanted && entry is JsonObject held)
            {
                return wanted.Count > 0 && wanted.All(member =>
                {
                    var sub = ScimResourceSchema.FindSubAttribute(attribute, member.Key);
                    var heldValue = ScimEntryFilter.Member(held, member.Key);
                    return heldValue != null && member.Value != null && SameValue(heldValue, member.Value, sub?.CaseExact ?? false);
                });
            }

            return item != null && entry != null && SameValue(entry, item, attribute.CaseExact);
        }

        private static bool SameValue(JsonNode held, JsonNode wanted, bool caseExact)
        {
            if (JsonNode.DeepEquals(held, wanted))
                return true;

            return held.GetValueKind() == JsonValueKind.String && wanted.GetValueKind() == JsonValueKind.String &&
                   string.Equals(held.GetValue<string>(), wanted.GetValue<string>(), caseExact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Lays an object's members onto a complex value, each replacing the sub-attribute it names.
        /// </summary>
        private void Merge(JsonObject target, ScimSchemaAttribute attribute, JsonObject members)
        {
            foreach (var (name, value) in members)
            {
                var sub = ScimResourceSchema.FindSubAttribute(attribute, name);
                if (sub == null)
                {
                    Coercions.Add($"The sub-attribute '{attribute.Name}.{name}' is not defined and was ignored.");
                    Ignore($"{attribute.Name}.{name}");
                    continue;
                }

                Guard(sub, attribute);
                if (ScimResourceSchema.IsReadOnly(sub))
                {
                    Coercions.Add($"The read-only sub-attribute '{attribute.Name}.{sub.Name}' was ignored.");
                    continue;
                }

                ApplyAttribute(target, sub, ScimPatchOperations.Replace, value, $"{attribute.Name}.{sub.Name}");
            }
        }

        /// <summary>
        /// One value of a multi-valued attribute: a typed simple value, or an entry carrying only the sub-attributes a
        /// client may set.
        /// </summary>
        private JsonNode Element(JsonNode item, ScimSchemaAttribute attribute, string label)
        {
            if (!IsComplex(attribute))
                return Coerce(item, attribute, label);

            if (item is not JsonObject members)
                throw new ScimPatchRefusal(ScimErrorTypes.InvalidValue, $"The entries of '{label}' take an object value.");

            var entry = new JsonObject();
            Merge(entry, attribute, members);
            return entry;
        }

        /// <summary>
        /// One simple value, checked against the attribute's type. A Boolean sent as a string is accepted (D6); anything
        /// else of the wrong type fails the request, naming the attribute and not the value.
        /// </summary>
        private JsonNode Coerce(JsonNode value, ScimSchemaAttribute attribute, string label)
        {
            if (IsComplex(attribute))
                throw new ScimPatchRefusal(ScimErrorTypes.InvalidValue, $"The attribute '{label}' is complex and takes an object value.");

            var kind = value.GetValueKind();
            switch (attribute.Type)
            {
                case ScimAttributeTypes.Boolean:
                    if (!ScimEntryFilter.TryBoolean(value, out var boolean))
                        throw Invalid(label);
                    if (kind == JsonValueKind.String)
                        Coercions.Add($"A Boolean sent as a string was read as one for '{label}'.");
                    return JsonValue.Create(boolean);

                case ScimAttributeTypes.Integer:
                    if (kind != JsonValueKind.Number || !long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        throw Invalid(label);
                    return value.DeepClone();

                case ScimAttributeTypes.Decimal:
                    if (kind != JsonValueKind.Number)
                        throw Invalid(label);
                    return value.DeepClone();

                case ScimAttributeTypes.DateTime:
                    if (kind != JsonValueKind.String || !ScimEntryFilter.TryInstant(value.GetValue<string>(), out _))
                        throw Invalid(label);
                    return value.DeepClone();

                default:
                    if (kind != JsonValueKind.String)
                        throw Invalid(label);
                    return value.DeepClone();
            }
        }

        /// <summary>
        /// The one value a single-valued target takes: a single-element array is unwrapped (D6); more fail the request.
        /// </summary>
        private JsonNode? Single(JsonNode? value, string label)
        {
            if (value is not JsonArray array)
                return value;

            if (array.Count != 1)
                throw new ScimPatchRefusal(ScimErrorTypes.InvalidValue, $"The attribute '{label}' takes one value.");

            Coercions.Add($"A single-element array was read as the one value of '{label}'.");
            return array[0];
        }

        private static ScimPatchRefusal Invalid(string label) =>
            new(ScimErrorTypes.InvalidValue, $"The value given for '{label}' is not of the attribute's type.");

        /// <summary>
        /// Refuses a write-only attribute wherever a request names it (D5), before anything is applied, since the request
        /// then fails whole.
        /// </summary>
        private static void Guard(ScimSchemaAttribute attribute, ScimSchemaAttribute? parent = null)
        {
            if (ScimResourceSchema.IsWriteOnly(attribute) || (parent != null && ScimResourceSchema.IsWriteOnly(parent)))
            {
                var name = parent == null ? attribute.Name : $"{parent.Name}.{attribute.Name}";
                throw new ScimPatchRefusal(ScimErrorTypes.InvalidValue, $"The attribute '{name}' is write-only, and this service provider does not accept it.");
            }
        }
        #endregion

        #region containers
        /// <summary>
        /// Where an attribute lives: the resource itself, or the member named by its extension's URN (RFC 7643 section 3).
        /// </summary>
        private JsonObject? Container(JsonObject resource, string? schemaUrn, bool create)
        {
            if (schemaUrn == null)
                return resource;

            var key = ScimEntryFilter.Key(resource, schemaUrn);
            if (key != null && resource[key] is JsonObject extension)
                return extension;

            if (!create)
                return null;

            if (key != null)
                resource.Remove(key);

            var created = new JsonObject();
            resource[schema.CanonicalUrn(schemaUrn)] = created;
            return created;
        }

        private static void RemoveIfEmpty(JsonObject resource, string? schemaUrn)
        {
            if (schemaUrn == null)
                return;

            var key = ScimEntryFilter.Key(resource, schemaUrn);
            if (key != null && resource[key] is JsonObject { Count: 0 })
                resource.Remove(key);
        }

        /// <summary>
        /// The array of a multi-valued attribute. A single bare value where the schema says multi-valued is kept as the
        /// first entry rather than replaced.
        /// </summary>
        private static JsonArray? ExistingArray(JsonObject container, ScimSchemaAttribute attribute)
        {
            var key = ScimEntryFilter.Key(container, attribute.Name!);
            if (key == null)
                return null;

            if (container[key] is JsonArray existing)
                return existing;

            var bare = container[key]?.DeepClone();
            container.Remove(key);
            var wrapped = bare == null ? new JsonArray() : new JsonArray(bare);
            container[attribute.Name!] = wrapped;
            return wrapped;
        }

        private static JsonArray CreateArray(JsonObject container, ScimSchemaAttribute attribute)
        {
            var created = new JsonArray();
            container[attribute.Name!] = created;
            return created;
        }

        private static void RemoveIfEmptyArray(JsonObject container, ScimSchemaAttribute attribute)
        {
            var key = ScimEntryFilter.Key(container, attribute.Name!);
            if (key != null && container[key] is JsonArray { Count: 0 })
                container.Remove(key);
        }
        #endregion

        private static bool IsComplex(ScimSchemaAttribute attribute) =>
            string.Equals(attribute.Type, ScimAttributeTypes.Complex, StringComparison.OrdinalIgnoreCase) || attribute.SubAttributes.Count > 0;

        private static bool IsSelector(string name) =>
            string.Equals(name, TypeSubAttribute, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, PrimarySubAttribute, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// No value: JSON null, or an empty string, which identity providers send meaning none (D6).
        /// </summary>
        private bool IsAbsent(JsonNode? value)
        {
            if (value == null)
                return true;

            if (value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length == 0)
            {
                Coercions.Add("An empty string was read as no value.");
                return true;
            }

            return value.GetValueKind() == JsonValueKind.Null;
        }
    }
}
