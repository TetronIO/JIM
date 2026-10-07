// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

/// <summary>
/// Collision Remediation's write to the Metaverse (Unique Value Generation, #242, release 4; plan decision 8): a
/// generated value a target rejected, revised outside any synchronisation. Kept as its own partial file.
/// </summary>
public partial class MetaverseServer
{
    /// <summary>
    /// The name a Collision Remediation revision is recorded under on a Metaverse Object's change history.
    /// </summary>
    public const string CollisionRemediationInitiatorName = "Collision Remediation";

    /// <summary>
    /// Revises a generated value on a Metaverse Object after an export rejected it as already in use: records the
    /// attribute change on the object's history (when Metaverse Object change tracking is on), writes the value with the
    /// generated mapping's provenance under an optimistic concurrency check (the object must still hold
    /// <see cref="GeneratedValueRevision.PreviousValue"/>), and, in the same transaction, the assignment and the
    /// revision-pending record that carries the change to the queued exports at the next synchronisation.
    /// <para>
    /// Takes the repository to write through rather than using this server's own, because an export run's parallel
    /// batches each write through a context of their own; this method touches nothing else on the application, so it
    /// is safe to call from any of them at once.
    /// </para>
    /// </summary>
    /// <param name="repository">The repository of the unit of work doing the revision.</param>
    /// <param name="metaverseObject">The object, with its attribute values as read before the revision. Not modified.</param>
    /// <param name="attribute">The generated attribute being revised.</param>
    /// <param name="revision">The revision: the assignment as revised, the previous value, and the revision-pending record.</param>
    /// <param name="recordChange">Whether Metaverse Object change tracking is on, read once by the caller.</param>
    public Task<GeneratedValueRevisionResult> ReviseGeneratedValueAsync(
        ISyncRepository repository,
        MetaverseObject metaverseObject,
        MetaverseAttribute attribute,
        GeneratedValueRevision revision,
        bool recordChange)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(metaverseObject);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(revision);

        var previous = new MetaverseObjectAttributeValue
        {
            Attribute = attribute,
            AttributeId = attribute.Id,
            MetaverseObject = metaverseObject,
            StringValue = revision.NewNumericValue.HasValue ? null : revision.PreviousValue,
            IntValue = revision.NewNumericValue.HasValue && !revision.IsLongNumber ? (int?)revision.PreviousNumericValue : null,
            LongValue = revision.NewNumericValue.HasValue && revision.IsLongNumber ? revision.PreviousNumericValue : null,
            ContributedBySyncRuleId = revision.ContributedBySyncRuleId,
            ContributedBySystemId = revision.ContributedBySystemId
        };
        var revised = new MetaverseObjectAttributeValue
        {
            Attribute = attribute,
            AttributeId = attribute.Id,
            MetaverseObject = metaverseObject,
            StringValue = revision.NewNumericValue.HasValue ? null : revision.Assignment.Value,
            IntValue = revision.NewNumericValue.HasValue && !revision.IsLongNumber ? (int?)revision.NewNumericValue : null,
            LongValue = revision.NewNumericValue.HasValue && revision.IsLongNumber ? revision.NewNumericValue : null,
            ContributedBySyncRuleId = revision.ContributedBySyncRuleId,
            ContributedBySystemId = revision.ContributedBySystemId
        };

        MetaverseObjectChange? change = null;
        if (recordChange)
        {
            change = new MetaverseObjectChange
            {
                MetaverseObject = metaverseObject,
                ChangeType = ObjectChangeType.Updated,
                ChangeTime = DateTime.UtcNow,
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = CollisionRemediationInitiatorName,
                ChangeInitiatorType = MetaverseObjectChangeInitiatorType.System
            };
            change.AddAttributeValueChange(previous, ValueChangeType.Remove);
            change.AddAttributeValueChange(revised, ValueChangeType.Add);
        }

        // The denormalised name follows the revision when the object is named by this attribute, computed over a copy
        // so the caller's object is left exactly as it was read.
        string? cachedDisplayName = null;
        if (ObjectNaming.IsMetaverseNameAttribute(attribute.Name))
        {
            var values = metaverseObject.AttributeValues.Where(av => av.AttributeId != attribute.Id).Append(revised);
            cachedDisplayName = ObjectNaming.MetaverseNameFrom(values);
        }

        return repository.ApplyGeneratedValueRevisionAsync(revision with
        {
            MetaverseObjectChange = change,
            CachedDisplayName = cachedDisplayName
        });
    }
}
