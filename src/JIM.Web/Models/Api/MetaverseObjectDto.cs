// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
using JIM.Utilities;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Metaverse Object in full: its attribute values, where each came from, and the Connected System Objects joined
/// to it.
/// </summary>
public class MetaverseObjectDto
{
    /// <summary>
    /// The unique identifier of the Metaverse Object.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// When the Metaverse Object was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the Metaverse Object last changed (UTC).
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The kind of principal behind the earliest recorded change (User, ApiKey or System), with CreatedById and
    /// CreatedByName; null when no change history is recorded, as when change tracking is off or the history has
    /// been purged.
    /// </summary>
    public ActivityInitiatorType? CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal behind the earliest recorded change; null when no change history is
    /// recorded.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>The name of the principal behind the earliest recorded change, as it was then.</summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// The kind of principal behind the latest recorded change (User, ApiKey or System), with LastUpdatedById and
    /// LastUpdatedByName; null when no change history is recorded.
    /// </summary>
    public ActivityInitiatorType? LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal behind the latest recorded change; null when no change history is
    /// recorded.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>The name of the principal behind the latest recorded change, as it was then.</summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The Metaverse Object's display name; null when it has none.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether the Metaverse Object is in use (Normal) or has been marked Obsolete.
    /// </summary>
    public MetaverseObjectStatus Status { get; set; }

    /// <summary>
    /// How the Metaverse Object came to exist: Projected from a Connected System Object, or Internal, created in
    /// JIM itself. JIM never deletes an Internal object automatically.
    /// </summary>
    public MetaverseObjectOrigin Origin { get; set; }

    /// <summary>
    /// When the last Connected System Object was disconnected from the Metaverse Object (UTC); null while one is
    /// still joined.
    /// </summary>
    public DateTime? LastConnectorDisconnectedDate { get; set; }

    /// <summary>
    /// Whether the Metaverse Object is due to be deleted under its Object Type's deletion rule.
    /// </summary>
    public bool IsPendingDeletion { get; set; }

    /// <summary>
    /// When the Metaverse Object becomes due for deletion, once its grace period ends (UTC); null when it is not
    /// pending deletion.
    /// </summary>
    public DateTime? DeletionEligibleDate { get; set; }

    /// <summary>
    /// The Connected System whose disconnection made the Metaverse Object pending deletion. Null when it is not
    /// pending deletion, when no single system triggered it (its last connection went without one), or when the
    /// deletion was scheduled before JIM recorded the trigger.
    /// </summary>
    public string? DeletionTriggeredBySystemName { get; set; }

    /// <summary>
    /// The Metaverse Object Type, with its Id and Name.
    /// </summary>
    public MetaverseObjectTypeDto Type { get; set; } = null!;

    /// <summary>
    /// Every value the Metaverse Object holds, one entry per value, each with the attribute it belongs to and the
    /// Connected System and Synchronisation Rule that contributed it.
    /// </summary>
    public List<MetaverseObjectAttributeValueDto> AttributeValues { get; set; } = new();

    /// <summary>
    /// Every Connected System Object joined to the Metaverse Object, with how it was joined and the state of its
    /// connection, as the portal's Connections tab shows them.
    /// </summary>
    public List<MetaverseObjectConnectionDto> ConnectedSystemObjects { get; set; } = new();

    /// <summary>
    /// Creates a DTO from a MetaverseObject entity.
    /// </summary>
    /// <param name="entity">The Metaverse Object, loaded with provenance.</param>
    /// <param name="connections">
    /// The object's joined Connected System Objects, from
    /// <c>MetaverseServer.GetMetaverseObjectConnectionsAsync</c>: the one derivation of role and State, shared
    /// with the portal's Connections tab so the two surfaces cannot drift apart. It is also the fresher read of
    /// what is joined (it runs after the object itself is loaded), so the rows come from it rather than from the
    /// entity's own navigation; the entity supplies only each row's best-ranked name, which it alone carries.
    /// </param>
    /// <param name="initiators">Who made the object's earliest and latest changes, from
    /// <c>MetaverseServer.GetMetaverseObjectChangeInitiatorsAsync</c>: Created By and Last Updated By, as the portal's
    /// Properties tab shows them.</param>
    public static MetaverseObjectDto FromEntity(MetaverseObject entity, IReadOnlyCollection<MetaverseObjectConnection> connections,
        (MvoChangeInitiatorSummary? Earliest, MvoChangeInitiatorSummary? Latest) initiators = default)
    {
        var namesByConnectedSystemObjectId = entity.ConnectedSystemObjects
            .Where(cso => !string.IsNullOrEmpty(cso.NameOrId))
            .ToDictionary(cso => cso.Id, cso => cso.NameOrId);
        var statusesByConnectedSystemObjectId = entity.ConnectedSystemObjects
            .ToDictionary(cso => cso.Id, cso => (cso.Status, cso.DateJoined));

        return new MetaverseObjectDto
        {
            Id = entity.Id,
            Created = entity.Created,
            LastUpdated = entity.LastUpdated,
            CreatedByType = initiators.Earliest?.InitiatedByType,
            CreatedById = initiators.Earliest?.InitiatedById,
            CreatedByName = initiators.Earliest?.InitiatedByName,
            LastUpdatedByType = initiators.Latest?.InitiatedByType,
            LastUpdatedById = initiators.Latest?.InitiatedById,
            LastUpdatedByName = initiators.Latest?.InitiatedByName,
            DisplayName = entity.Name,
            Status = entity.Status,
            Origin = entity.Origin,
            LastConnectorDisconnectedDate = entity.LastConnectorDisconnectedDate,
            IsPendingDeletion = entity.IsPendingDeletion,
            DeletionEligibleDate = entity.DeletionEligibleDate,
            DeletionTriggeredBySystemName = entity.DeletionTriggeredBySystemName,
            Type = MetaverseObjectTypeDto.FromEntity(entity.Type),
            AttributeValues = entity.AttributeValues
                .Select(MetaverseObjectAttributeValueDto.FromEntity)
                .ToList(),
            ConnectedSystemObjects = connections
                .Select(connection => MetaverseObjectConnectionDto.FromConnection(
                    connection,
                    namesByConnectedSystemObjectId.GetValueOrDefault(connection.ConnectedSystemObjectId),
                    statusesByConnectedSystemObjectId.TryGetValue(connection.ConnectedSystemObjectId, out var csoState) ? csoState : default))
                .ToList()
        };
    }
}

/// <summary>
/// A Metaverse Object Type, by ID and name.
/// </summary>
public class MetaverseObjectTypeDto
{
    /// <summary>
    /// The unique identifier of the Object Type.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The Object Type's name.
    /// </summary>
    public string Name { get; set; } = null!;

    public static MetaverseObjectTypeDto FromEntity(MetaverseObjectType entity)
    {
        return new MetaverseObjectTypeDto
        {
            Id = entity.Id,
            Name = entity.Name
        };
    }
}

/// <summary>
/// API representation of a MetaverseObjectAttributeValue.
/// </summary>
public class MetaverseObjectAttributeValueDto
{
    public Guid Id { get; set; }
    public int AttributeId { get; set; }
    public string AttributeName { get; set; } = null!;
    public AttributeDataType AttributeType { get; set; }
    public AttributePlurality AttributePlurality { get; set; }
    public string? StringValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public int? IntValue { get; set; }
    public long? LongValue { get; set; }
    public decimal? DecimalValue { get; set; }

    /// <summary>
    /// The value for Binary attributes. Serialised to JSON as a base64-encoded string
    /// (System.Text.Json's representation for byte arrays).
    /// </summary>
    public byte[]? ByteValue { get; set; }

    public Guid? GuidValue { get; set; }
    public bool? BoolValue { get; set; }
    public Guid? ReferenceValueId { get; set; }
    public string? ReferenceValueDisplayName { get; set; }
    public int? ContributedBySystemId { get; set; }
    public string? ContributedBySystemName { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose mapping won attribute priority resolution and contributed this value.
    /// Null when the value is managed internally, or when the contributing rule has since been deleted
    /// (ContributedBySystemId/Name remain as the denormalised record).
    /// </summary>
    public int? ContributedBySyncRuleId { get; set; }
    public string? ContributedBySyncRuleName { get; set; }

    /// <summary>
    /// When true, this row is an asserted null: a connected, in-scope contributor positively asserted
    /// "no value" for this attribute. All value fields are null and the row carries provenance only.
    /// Consumers must treat such a row as "no value present", not as a value; it exists so a deliberate
    /// assertion is distinguishable from a plain absence (no row at all).
    /// </summary>
    public bool NullValue { get; set; }

    public static MetaverseObjectAttributeValueDto FromEntity(MetaverseObjectAttributeValue entity)
    {
        return new MetaverseObjectAttributeValueDto
        {
            Id = entity.Id,
            AttributeId = entity.AttributeId,
            AttributeName = entity.Attribute?.Name ?? string.Empty,
            AttributeType = entity.Attribute?.Type ?? AttributeDataType.NotSet,
            AttributePlurality = entity.Attribute?.AttributePlurality ?? AttributePlurality.SingleValued,
            StringValue = entity.StringValue,
            DateTimeValue = entity.DateTimeValue.HasValue ? DateTimeAttributeValue.ToCanonical(entity.DateTimeValue.Value) : null,
            IntValue = entity.IntValue,
            LongValue = entity.LongValue,
            DecimalValue = entity.DecimalValue,
            ByteValue = entity.ByteValue,
            GuidValue = entity.GuidValue,
            BoolValue = entity.BoolValue,
            ReferenceValueId = entity.ReferenceValueId,
            ReferenceValueDisplayName = entity.ReferenceValue?.Name,
            ContributedBySystemId = entity.ContributedBySystemId,
            ContributedBySystemName = entity.ContributedBySystem?.Name,
            ContributedBySyncRuleId = entity.ContributedBySyncRuleId,
            ContributedBySyncRuleName = entity.ContributedBySyncRule?.Name,
            NullValue = entity.NullValue
        };
    }
}

/// <summary>
/// One Connected System Object joined to a Metaverse Object, with the join data the portal's Connections
/// tab shows: the owning Connected System, the object's role in synchronisation, how it was joined, and its
/// derived connection State (#1519).
/// </summary>
public class MetaverseObjectConnectionDto
{
    /// <summary>The Connected System Object's unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The unique identifier of the Connected System that holds the object.</summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>The name of the Connected System that holds the object.</summary>
    public string ConnectedSystemName { get; set; } = null!;

    /// <summary>The object's best-ranked name, falling back to its external id.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The Connected System Object Type's name (for example "user").</summary>
    public string ObjectTypeName { get; set; } = null!;

    /// <summary>How the object was joined to the Metaverse Object.</summary>
    public ConnectedSystemObjectJoinType JoinType { get; set; }

    /// <summary>When the object was joined to the Metaverse Object, where that is recorded.</summary>
    public DateTime? DateJoined { get; set; }

    /// <summary>The object's own status in the Connector Space.</summary>
    public ConnectedSystemObjectStatus Status { get; set; }

    /// <summary>
    /// The derived connection state: the object's status combined with any Pending Export's change type and
    /// status (in sync, an update or provisioning pending, an export failed, obsolete, and so on).
    /// </summary>
    public ConnectedSystemObjectConnectionState State { get; set; }

    /// <summary>
    /// True when at least one enabled Import Synchronisation Rule exists for this object's Connected System
    /// and Connected System Object Type: values can flow inbound from it.
    /// </summary>
    public bool IsSource { get; set; }

    /// <summary>
    /// True when at least one enabled Export Synchronisation Rule exists for this object's Connected System
    /// and Connected System Object Type: values can flow outbound to it.
    /// </summary>
    public bool IsTarget { get; set; }

    /// <summary>
    /// The number of attribute changes on the object's Pending Export when one is queued and carries attribute
    /// changes (an update in progress); null otherwise, so a caller never reads "0 attributes" for a state that
    /// is not about attribute changes at all.
    /// </summary>
    public int? PendingAttributeChangeCount { get; set; }

    /// <summary>When the object was last synchronised.</summary>
    public DateTime? LastSynchronised { get; set; }

    /// <summary>
    /// Maps one derived connection, preferring the name the Metaverse Object's own graph carries: the
    /// connections derivation loads only identifying values (a group can hold thousands), so it names a row by
    /// its external id, where the detail response has always used the object's best-ranked name.
    /// </summary>
    public static MetaverseObjectConnectionDto FromConnection(
        MetaverseObjectConnection connection,
        string? bestRankedName,
        (ConnectedSystemObjectStatus Status, DateTime? DateJoined) connectedSystemObject)
    {
        return new MetaverseObjectConnectionDto
        {
            Id = connection.ConnectedSystemObjectId,
            ConnectedSystemId = connection.ConnectedSystemId,
            ConnectedSystemName = connection.ConnectedSystemName,
            DisplayName = bestRankedName ?? connection.DisplayName,
            ObjectTypeName = connection.ObjectTypeName,
            JoinType = connection.JoinType,
            DateJoined = connectedSystemObject.DateJoined,
            Status = connectedSystemObject.Status,
            State = connection.State,
            IsSource = connection.IsSource,
            IsTarget = connection.IsTarget,
            PendingAttributeChangeCount = connection.PendingAttributeChangeCount,
            LastSynchronised = connection.LastSynchronised
        };
    }
}

/// <summary>
/// A Metaverse Object pending deletion.
/// </summary>
public class PendingDeletionDto
{
    /// <summary>The unique identifier of the Metaverse Object.</summary>
    public Guid Id { get; set; }

    /// <summary>The display name of the Metaverse Object.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The Metaverse Object Type, with its Id and Name.
    /// </summary>
    /// <remarks>
    /// Nested to match the single-object response shape.
    /// </remarks>
    public MetaverseObjectTypeDto Type { get; set; } = null!;

    /// <summary>
    /// When the last Connected System Object was disconnected from the Metaverse Object (UTC).
    /// </summary>
    public DateTime LastConnectorDisconnectedDate { get; set; }

    /// <summary>
    /// When the Metaverse Object becomes due for deletion, once its grace period ends (UTC).
    /// </summary>
    public DateTime? DeletionEligibleDate { get; set; }

    /// <summary>
    /// How many days remain until the Metaverse Object is due for deletion; negative once it is overdue.
    /// </summary>
    public int? DaysUntilDeletion { get; set; }

    /// <summary>
    /// The grace period its Object Type sets.
    /// </summary>
    public TimeSpan? GracePeriod { get; set; }

    /// <summary>
    /// How many Connected System Objects are still joined to the Metaverse Object.
    /// </summary>
    public int ConnectedSystemObjectCount { get; set; }

    /// <summary>
    /// Where the deletion stands: Deprovisioning while Connected System Objects remain joined, AwaitingGracePeriod
    /// once none remain but the grace period is still running, and ReadyForDeletion once it has ended.
    /// </summary>
    public string Status { get; set; } = null!;

    /// <summary>
    /// The unique identifier of the Connected System whose disconnection scheduled the deletion; null when no
    /// single system triggered it, or when it was scheduled before JIM recorded the trigger.
    /// </summary>
    public int? DeletionTriggeredBySystemId { get; set; }

    /// <summary>
    /// That Connected System's name, captured when the deletion was scheduled so it survives the system's own
    /// deletion.
    /// </summary>
    public string? DeletionTriggeredBySystemName { get; set; }

    /// <summary>
    /// Why the deletion rule marked the Metaverse Object for deletion. NoConnectorRemainsStateConvergence means JIM
    /// found it with nothing joined rather than through one system's disconnection, which is why
    /// DeletionTriggeredBySystemName is null; NotSet when the deletion was scheduled before JIM recorded a reason.
    /// </summary>
    public CausalReasonCode ReasonCode { get; set; } = CausalReasonCode.NotSet;

    /// <summary>
    /// Creates a DTO from a MetaverseObject entity.
    /// </summary>
    public static PendingDeletionDto FromEntity(MetaverseObject entity)
    {
        var connectorCount = entity.ConnectedSystemObjects?.Count ?? 0;
        var reasonCode = MvoDeletionPolicySnapshot.FromJson(entity.DeletionPolicySnapshotJson)?.ReasonCode ?? CausalReasonCode.NotSet;
        var daysUntilDeletion = entity.DeletionEligibleDate.HasValue
            ? (int)Math.Ceiling((entity.DeletionEligibleDate.Value - DateTime.UtcNow).TotalDays)
            : (int?)null;

        // Determine status
        string status;
        if (connectorCount > 0)
        {
            status = "Deprovisioning";
        }
        else if (daysUntilDeletion.HasValue && daysUntilDeletion.Value > 0)
        {
            status = "AwaitingGracePeriod";
        }
        else
        {
            status = "ReadyForDeletion";
        }

        return new PendingDeletionDto
        {
            Id = entity.Id,
            DisplayName = entity.Name,
            Type = new MetaverseObjectTypeDto
            {
                Id = entity.Type?.Id ?? 0,
                Name = entity.Type?.Name ?? "Unknown"
            },
            LastConnectorDisconnectedDate = entity.LastConnectorDisconnectedDate!.Value,
            DeletionEligibleDate = entity.DeletionEligibleDate,
            DaysUntilDeletion = daysUntilDeletion,
            GracePeriod = entity.Type?.DeletionGracePeriod,
            ConnectedSystemObjectCount = connectorCount,
            Status = status,
            DeletionTriggeredBySystemId = entity.DeletionTriggeredBySystemId,
            DeletionTriggeredBySystemName = entity.DeletionTriggeredBySystemName,
            ReasonCode = reasonCode
        };
    }
}

/// <summary>
/// How many Metaverse Objects are pending deletion, and where their deletions stand.
/// </summary>
public class PendingDeletionSummary
{
    /// <summary>
    /// How many Metaverse Objects are pending deletion.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// How many still have Connected System Objects joined, awaiting deprovisioning.
    /// </summary>
    public int DeprovisioningCount { get; set; }

    /// <summary>
    /// How many have nothing joined but are still within their grace period.
    /// </summary>
    public int AwaitingGracePeriodCount { get; set; }

    /// <summary>
    /// How many are due for deletion: their grace period has ended and nothing is joined.
    /// </summary>
    public int ReadyForDeletionCount { get; set; }
}
