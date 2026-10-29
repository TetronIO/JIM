// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Models.Core;

/// <summary>
/// What a write outside synchronisation changed on a Metaverse Object, stored beside its
/// <see cref="MetaverseObject.ScopeReviewPending"/> flag (<see cref="MetaverseObject.UnsynchronisedChangesJson"/>) until
/// the next synchronisation of any Connected System has delivered it to the target systems (#2615). A sign-in refreshing
/// a person's email, a person's profile edited in the portal, a value set through the API: none of these is visited by a
/// synchronisation, so without this record nothing would stage the new value for an account that already exists.
/// <para>
/// It records the attributes that changed and the values that were removed. The values that were added are not stored:
/// the review reads them from the object, which holds them. The removals have to be stored because the object no longer
/// does, and because a removal is what a multi-valued attribute exports as a Remove and a single-valued attribute as a
/// clear.
/// </para>
/// </summary>
public class MetaverseObjectUnsynchronisedChanges
{
    // Follows the stored-JSON conventions of MvoDeletionPolicySnapshot: camelCase properties, nulls omitted.
    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// The Metaverse Attributes whose values changed (added, changed or removed).
    /// </summary>
    public List<int> AttributeIds { get; set; } = [];

    /// <summary>
    /// The values removed, each of which export staging stages as a Remove (multi-valued) or a clear (single-valued)
    /// unless the object holds the value again when the review runs.
    /// </summary>
    public List<MetaverseObjectUnsynchronisedRemoval> Removals { get; set; } = [];

    /// <summary>
    /// Reads the stored document.
    /// </summary>
    /// <param name="json">The stored value of <see cref="MetaverseObject.UnsynchronisedChangesJson"/>.</param>
    /// <returns>The changes, or null when nothing is stored (a flag raised by an export Synchronisation Rule's
    /// configuration change, or by the Temporal Scope Reconciler, carries none).</returns>
    /// <exception cref="JsonException">The stored document is not a valid record.</exception>
    public static MetaverseObjectUnsynchronisedChanges? FromJson(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<MetaverseObjectUnsynchronisedChanges>(json, SerialiserOptions);

    /// <summary>
    /// The document to store.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerialiserOptions);

    /// <summary>
    /// Merges one write's changes into what is already recorded: the attributes are unioned and the removals appended,
    /// each once. Order never matters: a value removed and then given again is reconciled by the review, which reads
    /// what the object holds when it runs.
    /// </summary>
    /// <param name="additions">The values the write added; only their attributes are recorded.</param>
    /// <param name="removals">The values the write removed.</param>
    public void Record(
        IEnumerable<MetaverseObjectAttributeValue>? additions,
        IEnumerable<MetaverseObjectAttributeValue>? removals)
    {
        foreach (var addition in additions ?? [])
            AddAttributeId(AttributeIdOf(addition));

        foreach (var removal in removals ?? [])
        {
            var attributeId = AttributeIdOf(removal);
            AddAttributeId(attributeId);

            // An asserted-null marker carries no value to remove.
            if (removal.NullValue)
                continue;

            var record = MetaverseObjectUnsynchronisedRemoval.From(removal, attributeId);
            if (record.HasContent && !Removals.Contains(record))
                Removals.Add(record);
        }
    }

    private void AddAttributeId(int attributeId)
    {
        if (!AttributeIds.Contains(attributeId))
            AttributeIds.Add(attributeId);
    }

    // The foreign key is only set once a value has been saved; a value built by a caller names its attribute by navigation.
    private static int AttributeIdOf(MetaverseObjectAttributeValue value)
        => value.AttributeId != 0 ? value.AttributeId : value.Attribute?.Id ?? 0;
}
