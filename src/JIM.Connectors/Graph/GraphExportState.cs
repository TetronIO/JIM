// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Connectors.Graph;

/// <summary>
/// One Pending Export as the Connector works through it: what it asks of Graph, the object's id once known, and how it
/// has fared. Built from the Pending Export before anything is sent, so one that cannot be written fails without a
/// request.
/// </summary>
internal sealed class GraphExportState
{
    private GraphExportState(PendingExport pendingExport)
    {
        PendingExport = pendingExport;
    }

    public PendingExport PendingExport { get; }

    public bool IsUser { get; private set; }

    public string Collection => IsUser ? "users" : "groups";

    /// <summary>The object's Graph id: its External ID, or the id Graph assigned when this export created it.</summary>
    public string? Id { get; set; }

    /// <summary>Whether this export created the object, after which it must report the new id however its follow-ups fare.</summary>
    public bool Created { get; set; }

    /// <summary>
    /// For a Create JIM sends again, the id an earlier send was given: the Connected System Object already carries it.
    /// JIM sends a Create again when a confirming import did not see the object, which Graph's lagging lists can cause
    /// moments after it was made, so the object may well exist.
    /// </summary>
    public string? PreviouslyCreatedId { get; private set; }

    /// <summary>The properties a create or update sends, already in Graph's shape.</summary>
    public JsonObject Properties { get; } = [];

    /// <summary>The manager to set; an empty string removes the manager; null leaves it alone.</summary>
    public string? Manager { get; private set; }

    /// <summary>Members or owners to add, by relationship, in the order asked.</summary>
    public Dictionary<string, List<string>> Additions { get; } = new(StringComparer.Ordinal);

    /// <summary>Members or owners to remove, by relationship.</summary>
    public Dictionary<string, List<string>> Removals { get; } = new(StringComparer.Ordinal);

    /// <summary>Relationships every value of which is to be removed, apart from what this export adds.</summary>
    public HashSet<string> RemoveAll { get; } = new(StringComparer.Ordinal);

    /// <summary>Why the export failed before or at its first step; null while it stands.</summary>
    public ConnectedSystemExportResult? Failure { get; set; }

    /// <summary>Follow-up steps that failed, each classified.</summary>
    public List<ConnectedSystemExportResult> FollowUpFailures { get; } = [];

    /// <summary>Whether a write set the account's <c>accountEnabled</c>.</summary>
    public bool SetsAccountEnabledState { get; set; }

    /// <summary>Works out what a Pending Export asks of Graph.</summary>
    public static GraphExportState Plan(PendingExport pendingExport)
    {
        var state = new GraphExportState(pendingExport);
        var objectTypeName = pendingExport.ConnectedSystemObject?.Type?.Name
                             ?? pendingExport.AttributeValueChanges.Select(c => c.Attribute.ConnectedSystemObjectType?.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (!string.Equals(objectTypeName, GraphSchemaCatalogue.UserObjectType, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(objectTypeName, GraphSchemaCatalogue.GroupObjectType, StringComparison.OrdinalIgnoreCase))
        {
            state.Failure = ConnectedSystemExportResult.Failed(
                $"The Microsoft Entra ID Connector writes users and groups; this change is for '{objectTypeName ?? "an unknown Object Type"}'.");
            return state;
        }

        state.IsUser = string.Equals(objectTypeName, GraphSchemaCatalogue.UserObjectType, StringComparison.OrdinalIgnoreCase);

        if (pendingExport.ChangeType == PendingExportChangeType.Create)
        {
            var known = pendingExport.ConnectedSystemObject?.ExternalIdAttributeValue?.StringValue;
            state.PreviouslyCreatedId = string.IsNullOrWhiteSpace(known) ? null : known;
        }
        else
        {
            state.Id = pendingExport.ConnectedSystemObject?.ExternalIdAttributeValue?.StringValue;
            if (string.IsNullOrWhiteSpace(state.Id))
            {
                state.Failure = ConnectedSystemExportResult.Failed(
                    "The Connected System Object has no External ID, so the Microsoft Entra ID object it stands for cannot be found. Import it before exporting changes to it.");
                return state;
            }
        }

        if (pendingExport.ChangeType == PendingExportChangeType.Delete)
            return state;

        try
        {
            foreach (var change in pendingExport.AttributeValueChanges)
                state.Apply(change);
        }
        catch (PartiallyLoadedObjectException ex)
        {
            state.Failure = ConnectedSystemExportResult.Failed(
                $"A multi-valued attribute could not be written: Microsoft Graph replaces such a value whole, and JIM did not load every value the object holds. {ex.Message}");
        }

        return state;
    }

    private void Apply(PendingExportAttributeValueChange change)
    {
        var name = change.Attribute.Name;
        if (name is GraphSchemaCatalogue.Id or GraphSchemaCatalogue.SourceOfAuthority)
            return;

        var value = ValueOf(change);
        switch (name)
        {
            case GraphSchemaCatalogue.Manager when IsUser:
                Manager = change.ChangeType is PendingExportAttributeChangeType.Remove or PendingExportAttributeChangeType.RemoveAll || value is not string managerId
                    ? ""
                    : managerId;
                return;

            case GraphSchemaCatalogue.Members or GraphSchemaCatalogue.Owners when !IsUser:
                ApplyRelationship(name, change, value as string);
                return;
        }

        var catalogue = (IsUser ? GraphSchemaCatalogue.UserAttributes : GraphSchemaCatalogue.GroupAttributes)
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        var property = catalogue?.SelectProperty ?? name;

        if (catalogue?.Member is { } member)
        {
            var nested = Properties[property] as JsonObject ?? [];
            nested[member] = IsRemoval(change) ? null : ToJson(value);
            Properties[property] = nested;
            return;
        }

        if (name.Equals("accountEnabled", StringComparison.OrdinalIgnoreCase))
            SetsAccountEnabledState = true;

        if ((catalogue?.Plurality ?? change.Attribute.AttributePlurality) == AttributePlurality.MultiValued)
        {
            Properties[property] = ApplyToWholeSet(property, name, change, value);
            return;
        }

        Properties[property] = IsRemoval(change) ? null : ToJson(value);
    }

    private void ApplyRelationship(string relationship, PendingExportAttributeValueChange change, string? reference)
    {
        if (change.ChangeType == PendingExportAttributeChangeType.RemoveAll)
        {
            RemoveAll.Add(relationship);
            Additions.Remove(relationship);
            Removals.Remove(relationship);
            return;
        }

        if (string.IsNullOrWhiteSpace(reference))
            return;

        var (into, outOf) = change.ChangeType == PendingExportAttributeChangeType.Remove
            ? (Removals, Additions)
            : (Additions, Removals);
        outOf.GetValueOrDefault(relationship)?.Remove(reference);
        var list = into.TryGetValue(relationship, out var existing) ? existing : into[relationship] = [];
        if (!list.Contains(reference, StringComparer.OrdinalIgnoreCase))
            list.Add(reference);
    }

    /// <summary>
    /// Graph replaces a multi-valued property whole, so the change is applied to every value the object holds (none, for
    /// a create) and the result sent.
    /// </summary>
    private JsonArray ApplyToWholeSet(string property, string attributeName, PendingExportAttributeValueChange change, object? value)
    {
        if (Properties[property] is not JsonArray values)
        {
            values = [];
            if (PendingExport.ChangeType != PendingExportChangeType.Create && PendingExport.ConnectedSystemObject is { } held)
            {
                foreach (var heldJson in held.HeldValuesOf(attributeName).Select(v => ToJson(ValueOf(v))))
                    values.Add(heldJson);
            }
        }

        var json = ToJson(value);
        switch (change.ChangeType)
        {
            case PendingExportAttributeChangeType.RemoveAll:
                values.Clear();
                break;
            case PendingExportAttributeChangeType.Remove:
                foreach (var match in values.Where(v => JsonNode.DeepEquals(v, json)).ToList())
                    values.Remove(match);
                break;
            default:
                if (json != null && !values.Any(v => JsonNode.DeepEquals(v, json)))
                    values.Add(json);
                break;
        }

        return values;
    }

    private static bool IsRemoval(PendingExportAttributeValueChange change) =>
        change.ChangeType is PendingExportAttributeChangeType.Remove or PendingExportAttributeChangeType.RemoveAll;

    /// <summary>The one value a change carries, whichever typed column holds it; a reference arrives as the target's Graph id.</summary>
    private static object? ValueOf(PendingExportAttributeValueChange change) =>
        (object?)change.StringValue ?? change.BoolValue ?? change.IntValue ?? change.LongValue ?? change.DateTimeValue
        ?? change.DecimalValue ?? change.GuidValue ?? (object?)change.ByteValue;

    private static object? ValueOf(ConnectedSystemObjectAttributeValue held) =>
        (object?)held.StringValue ?? held.BoolValue ?? held.IntValue ?? held.LongValue ?? held.DateTimeValue
        ?? held.DecimalValue ?? held.GuidValue ?? (object?)held.ByteValue;

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        Guid guid => JsonValue.Create(guid.ToString()),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        // Graph's DateTimeOffset properties take UTC with a Z.
        DateTime dateTime => JsonValue.Create(DateTime.SpecifyKind(dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : dateTime, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString())
    };
}
