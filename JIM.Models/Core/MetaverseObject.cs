using JIM.Models.Activities;
using JIM.Models.Security;
using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Staging;

namespace JIM.Models.Core;

public class MetaverseObject
{
    #region accessors
    public Guid Id { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? LastUpdated { get; set; }

    public MetaverseObjectType Type { get; set; } = null!;

    public List<MetaverseObjectAttributeValue> AttributeValues { get; set; } = new();

    public List<Role> Roles { get; set; } = null!;

    public MetaverseObjectStatus Status { get; set; } = MetaverseObjectStatus.Normal;

    /// <summary>
    /// When the last connector was disconnected from this MVO.
    /// Used with MetaverseObjectType.DeletionGracePeriod to calculate deletion eligibility.
    /// Null = MVO has active connectors or was never connected.
    /// </summary>
    public DateTime? LastConnectorDisconnectedDate { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Deletion initiator tracking - captures who triggered the deletion when MVO is marked for deferred deletion.
    // This is set when LastConnectorDisconnectedDate is populated, so housekeeping can preserve the audit trail.
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The type of security principal that initiated the deletion (when marked for deferred deletion).
    /// Populated when LastConnectorDisconnectedDate is set, used by housekeeping to preserve audit trail.
    /// </summary>
    public ActivityInitiatorType DeletionInitiatedByType { get; set; } = ActivityInitiatorType.NotSet;

    /// <summary>
    /// The unique identifier of the security principal that initiated the deletion.
    /// Populated when LastConnectorDisconnectedDate is set, used by housekeeping to preserve audit trail.
    /// </summary>
    public Guid? DeletionInitiatedById { get; set; }

    /// <summary>
    /// The display name of the security principal that initiated the deletion.
    /// Populated when LastConnectorDisconnectedDate is set, used by housekeeping to preserve audit trail.
    /// </summary>
    public string? DeletionInitiatedByName { get; set; }

    /// <summary>
    /// How this MVO was created - determines deletion rule applicability.
    /// Projected MVOs are subject to automatic deletion rules.
    /// Internal MVOs (admin, service accounts) are protected from automatic deletion.
    /// </summary>
    public MetaverseObjectOrigin Origin { get; set; } = MetaverseObjectOrigin.Projected;

    /// <summary>
    /// Concurrency token using PostgreSQL's xmin system column.
    /// </summary>
    public uint xmin { get; set; }

    public List<MetaverseObjectChange> Changes { get; set; } = new();

    /// <summary>
    /// Used by JIM.Application to determine what attribute values need adding and change-tracking.
    /// </summary>
    [NotMapped]
    public List<MetaverseObjectAttributeValue> PendingAttributeValueAdditions { get; set; } = new();

    /// <summary>
    /// Used by JIM.Application to determine what attribute values need removing and change-tracking.
    /// </summary>
    [NotMapped]
    public List<MetaverseObjectAttributeValue> PendingAttributeValueRemovals { get; set; } = new();

    /// <summary>
    /// Navigation link to any joined Connected System Objects.
    /// </summary>
    public List<ConnectedSystemObject> ConnectedSystemObjects { get; set; } = new ();

    [NotMapped]
    public string? DisplayName
    {
        get
        {
            if (AttributeValues.Count == 0)
                return null;

            var av = AttributeValues.SingleOrDefault(q => q.Attribute?.Name == Constants.BuiltInAttributes.DisplayName);
            if (av != null && !string.IsNullOrEmpty(av.StringValue))
                return av.StringValue;

            return null;
        }
    }

    /// <summary>
    /// Indicates if this MVO is pending deletion (has disconnection date and awaiting grace period expiry).
    /// </summary>
    [NotMapped]
    public bool IsPendingDeletion => LastConnectorDisconnectedDate.HasValue &&
        Origin == MetaverseObjectOrigin.Projected &&
        Type?.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected;

    /// <summary>
    /// The date when this MVO becomes eligible for deletion (after grace period expires).
    /// Null if not pending deletion or no grace period configured.
    /// </summary>
    [NotMapped]
    public DateTime? DeletionEligibleDate => IsPendingDeletion && Type?.DeletionGracePeriod.HasValue == true && Type.DeletionGracePeriod.Value > TimeSpan.Zero
        ? LastConnectorDisconnectedDate!.Value.Add(Type.DeletionGracePeriod.Value)
        : null;
    #endregion

    #region public methods
    public MetaverseObjectAttributeValue? GetAttributeValue(string name)
    {
        return AttributeValues.SingleOrDefault(q => q.Attribute?.Name == name);
    }

    public bool HasAttributeValue(string name)
    {
        return AttributeValues.Any(q => q.Attribute?.Name == name);
    }

    public override string ToString()
    {
        return $"{DisplayName} ({Id})";
    }
    #endregion
}