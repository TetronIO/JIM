// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a MetaverseObject for detail views.
/// </summary>
public class MetaverseObjectDto
{
    public Guid Id { get; set; }
    public DateTime Created { get; set; }
    public DateTime? LastUpdated { get; set; }
    public string? DisplayName { get; set; }
    public MetaverseObjectStatus Status { get; set; }
    public MetaverseObjectOrigin Origin { get; set; }
    public DateTime? LastConnectorDisconnectedDate { get; set; }
    public bool IsPendingDeletion { get; set; }
    public DateTime? DeletionEligibleDate { get; set; }

    /// <summary>
    /// The Connected System whose disconnection triggered a pending deletion, or null when the object is
    /// not pending deletion, was marked by the #1605 state-convergent zero-join pass (no single system
    /// triggered it), or predates trigger attribution.
    /// </summary>
    public string? DeletionTriggeredBySystemName { get; set; }

    public MetaverseObjectTypeDto Type { get; set; } = null!;
    public List<MetaverseObjectAttributeValueDto> AttributeValues { get; set; } = new();
    /// <summary>
    /// Every Connected System Object joined to this Metaverse Object, with the same derived join data the
    /// portal's Connections tab shows (#1519).
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
    public static MetaverseObjectDto FromEntity(MetaverseObject entity, IReadOnlyCollection<MetaverseObjectConnection> connections)
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
/// Lightweight API representation of a MetaverseObjectType.
/// </summary>
public class MetaverseObjectTypeDto
{
    public int Id { get; set; }
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
            DateTimeValue = entity.DateTimeValue,
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
/// API representation of a MetaverseObject pending deletion.
/// </summary>
public class PendingDeletionDto
{
    /// <summary>The unique identifier of the Metaverse Object.</summary>
    public Guid Id { get; set; }

    /// <summary>The display name of the Metaverse Object.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The object type (id and name). Nested to match the single-object response shape.</summary>
    public MetaverseObjectTypeDto Type { get; set; } = null!;

    /// <summary>When the last connector was disconnected from this MVO.</summary>
    public DateTime LastConnectorDisconnectedDate { get; set; }

    /// <summary>The date when this MVO becomes eligible for deletion (after grace period expires).</summary>
    public DateTime? DeletionEligibleDate { get; set; }

    /// <summary>Number of days remaining until deletion (negative if overdue).</summary>
    public int? DaysUntilDeletion { get; set; }

    /// <summary>The grace period configured for this object type.</summary>
    public TimeSpan? GracePeriod { get; set; }

    /// <summary>Number of Connected System Objects still linked to this MVO.</summary>
    public int ConnectedSystemObjectCount { get; set; }

    /// <summary>
    /// The deletion status: Deprovisioning (has remaining connectors), AwaitingGracePeriod (fully disconnected, waiting),
    /// or ReadyForDeletion (grace period expired, no connectors).
    /// </summary>
    public string Status { get; set; } = null!;

    /// <summary>
    /// The Connected System whose disconnection triggered the scheduled deletion (#119).
    /// Null for deletions scheduled before trigger recording existed.
    /// </summary>
    public int? DeletionTriggeredBySystemId { get; set; }

    /// <summary>
    /// The display name of the triggering Connected System, captured when the deletion was scheduled
    /// so it survives deletion of the system itself (#119).
    /// </summary>
    public string? DeletionTriggeredBySystemName { get; set; }

    /// <summary>
    /// Why the rule decided to mark this object for deletion (#1605), read back from the decision-time
    /// policy snapshot. <see cref="CausalReasonCode.NoConnectorRemainsStateConvergence"/> means the object
    /// was found by the state-convergent zero-join pass rather than a specific system's disconnection, so a
    /// null <see cref="DeletionTriggeredBySystemName"/> is honest, not a missing fact. <see cref="CausalReasonCode.NotSet"/>
    /// when the snapshot is absent or predates reason-code capture.
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
/// Summary statistics for pending deletions.
/// </summary>
public class PendingDeletionSummary
{
    /// <summary>Total count of MVOs pending deletion.</summary>
    public int TotalCount { get; set; }

    /// <summary>Count of MVOs still connected to other systems, awaiting cascade deletion.</summary>
    public int DeprovisioningCount { get; set; }

    /// <summary>Count of MVOs fully disconnected but waiting for grace period to expire.</summary>
    public int AwaitingGracePeriodCount { get; set; }

    /// <summary>Count of MVOs eligible for deletion (grace period expired, no connectors).</summary>
    public int ReadyForDeletionCount { get; set; }
}
