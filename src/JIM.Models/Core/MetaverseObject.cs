// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    /// The Connected System whose disconnection triggered the scheduled deletion (#119).
    /// Set when a deletion is scheduled; cleared with the other deletion markers. Makes grace period
    /// cancellation precise (only undoing the triggering disconnection cancels in Specific mode) and lets
    /// the Pending Deletions page show what triggered each scheduled deletion.
    /// </summary>
    public int? DeletionTriggeredBySystemId { get; set; }

    /// <summary>
    /// The display name of the triggering Connected System at the time the deletion was scheduled.
    /// The name snapshot survives deletion of the system itself (#119).
    /// </summary>
    public string? DeletionTriggeredBySystemName { get; set; }

    /// <summary>
    /// The decision-time deletion policy snapshot (a serialised <c>MvoDeletionPolicySnapshot</c>), captured
    /// when the deletion is scheduled so housekeeping can carry it onto the final deletion record after the
    /// grace period; the record then reflects the policy that scheduled the deletion, not the policy at
    /// execution time (#119).
    /// </summary>
    public string? DeletionPolicySnapshotJson { get; set; }

    /// <summary>
    /// How this MVO was created - determines deletion rule applicability.
    /// Projected MVOs are subject to automatic deletion rules.
    /// Internal MVOs (admin, service accounts) are protected from automatic deletion.
    /// </summary>
    public MetaverseObjectOrigin Origin { get; set; } = MetaverseObjectOrigin.Projected;

    /// <summary>
    /// Set by the Temporal Scope Reconciler when this object's relative-date (outbound / export) scope
    /// membership has flipped purely because the clock advanced, with no Metaverse data change. The flag
    /// lets export evaluation, which otherwise only considers changed Metaverse Objects, pass this object
    /// through for re-evaluation, then is cleared once it has been processed. Part of the flag-and-delegate
    /// model (issue #892): the reconciler only flags; the existing engine applies the correct outcome
    /// (provision, deprovision, Attribute Flow, etc.).
    /// </summary>
    public bool ScopeReviewPending { get; set; }

    /// <summary>
    /// UTC watermark of when the Temporal Scope Reconciler last evaluated this object's relative-date export
    /// scope. Bounds each reconciliation sweep to the objects whose temporal boundary could have crossed since
    /// they were last evaluated. Null until first reconciled.
    /// </summary>
    public DateTime? LastScopeEvaluatedAt { get; set; }

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

    /// <summary>
    /// Performance cache of the Display Name attribute value, used for efficient sorting at scale.
    /// Updated automatically by MetaverseServer (Create/Update) and the sync engine (ApplyPendingAttributeChanges).
    /// The canonical Display Name value lives in <see cref="AttributeValues"/>.
    /// </summary>
    public string? CachedDisplayName { get; set; }

    /// <summary>
    /// The object's name: the first present value from <see cref="ObjectNaming.MetaverseNameAttributes"/>,
    /// falling back to <see cref="CachedDisplayName"/> when attribute values are not loaded (the
    /// Metaverse list projects the cache without materialising them). Null when nothing resolves.
    /// <para>
    /// Use this when persisting a name alongside a separately persisted identifier, or feeding a
    /// nullable API field. For anything a person reads on screen use <see cref="NameOrId"/>.
    /// </para>
    /// </summary>
    [NotMapped]
    public string? Name
    {
        get
        {
            if (AttributeValues.Count == 0)
                return CachedDisplayName;

            return ObjectNaming.MetaverseNameFrom(AttributeValues) ?? CachedDisplayName;
        }
    }

    /// <summary>
    /// The best human-readable label for this object: its <see cref="Name"/>, else its id. Prefer this
    /// for display; prefer <see cref="Name"/> when the identifier is already surfaced separately.
    /// </summary>
    [NotMapped]
    public string NameOrId => ObjectNaming.FirstPresent(Name) ?? Id.ToString();

    /// <summary>
    /// Indicates if this MVO is pending deletion (has disconnection date and awaiting grace period expiry).
    /// Applies to both WhenLastConnectorDisconnected and WhenAuthoritativeSourceDisconnected deletion rules.
    /// </summary>
    [NotMapped]
    public bool IsPendingDeletion => LastConnectorDisconnectedDate.HasValue &&
        Origin == MetaverseObjectOrigin.Projected &&
        (Type?.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected ||
         Type?.DeletionRule == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected);

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
        return $"{Name} ({Id})";
    }
    #endregion
}