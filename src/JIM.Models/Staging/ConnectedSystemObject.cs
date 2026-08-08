// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Activities;
using JIM.Models.Core;
namespace JIM.Models.Staging;

public class ConnectedSystemObject
{
    #region accessors
    public Guid Id { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? LastUpdated { get; set; }

    public ConnectedSystemObjectType Type { get; set; } = null!;
    public int TypeId { get; set; }

    public ConnectedSystem ConnectedSystem { get; set; } = null!;
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The partition this CSO was imported from. Nullable for CSOs created before
    /// partition tracking was added, or for Connected Systems without partitions.
    /// Used to scope deletion detection during partition-scoped full imports,
    /// and as a prerequisite for future partition-scoped sync (#437) and export (#438).
    /// </summary>
    public ConnectedSystemPartition? Partition { get; set; }
    public int? PartitionId { get; set; }

    /// <summary>
    /// Backlink for Entity Framework navigation. Do not use.
    /// </summary>
    public List<ActivityRunProfileExecutionItem> ActivityRunProfileExecutionItems { get; } = new();

    /// <summary>
    /// The attribute that uniquely identifies this object in the Connected System.
    /// It should be immutable (not change for the lifetime of the object).
    /// The Connected System may author it and be made available to JIM after import, or you may specify it at
    /// provisioning time, depending on the needs of the Connected System.
    /// This is a convenience accessor. It's defined as a property on one of the Connected System Object Type
    /// attributes. i.e. ConnectedSystemObjectTypeAttribute.IsExternalId
    /// </summary>
    public int ExternalIdAttributeId { get; set; }

    /// <summary>
    /// The attribute that may also identify the object in a Connected System.
    /// Whether this exists depends on if the Connected System supports secondary external ids or not.
    /// For instance, an LDAP system will use the DN for references to other objects, even though this is not a good
    /// identifier as it's not immutable.
    /// </summary>
    public int? SecondaryExternalIdAttributeId { get; set; }

    public List<ConnectedSystemObjectAttributeValue> AttributeValues { get; set; } = new();

    /// <summary>
    /// SPEC-1082: a content hash (not an identifier) of the import object that last brought this
    /// CSO's attribute values up to date, truncated SHA-256 stored as a <see cref="Guid"/> for
    /// storage efficiency. It is an admission ticket to SKIP hydration and diffing on a subsequent
    /// Full Import when the incoming payload hashes identically, never an input to correctness: a
    /// mismatch (including null, for CSOs never stamped) always falls through to the honest diff.
    /// <para>
    /// <b>Stamp-ordering invariant:</b> written by EXACTLY ONE code path,
    /// <c>ISyncRepository.StampImportStateAsync</c>, and ONLY after the batch's attribute-value
    /// writes for this CSO have committed. Any other writer of
    /// <see cref="ConnectedSystemObjectAttributeValue"/> rows for this CSO MUST null this column in
    /// the same operation (see SPEC-1082 D9); a stale non-null hash surviving a value mutation
    /// would be a permanently believable lie about the object's content.
    /// </para>
    /// </summary>
    public Guid? ImportStateHash { get; set; }

    /// <summary>
    /// SPEC-1082: a hash of this CSO's object type schema shape (attribute names, types,
    /// plurality, selection) at the time <see cref="ImportStateHash"/> was stamped, plus the
    /// content-hash algorithm version. Compared against the CURRENT type fingerprint at skip time;
    /// a mismatch disqualifies the skip lazily (schema redefinition or algorithm bump) without any
    /// mass-invalidation write. Follows the same stamp-ordering invariant as
    /// <see cref="ImportStateHash"/>.
    /// </summary>
    public Guid? ImportStateFingerprint { get; set; }

    /// <summary>
    /// Transient (SPEC-1082 D7): the import processor sets this when it wants
    /// <see cref="ImportStateHash"/> stamped after this CSO's batch write commits. Never persisted
    /// directly; consumed by the save phase to build the batch's <c>StampImportStateAsync</c> call.
    /// </summary>
    [NotMapped]
    public Guid? PendingImportStateHash { get; set; }

    /// <summary>
    /// Transient (SPEC-1082 D7): the fingerprint to stamp alongside <see cref="PendingImportStateHash"/>.
    /// </summary>
    [NotMapped]
    public Guid? PendingImportStateFingerprint { get; set; }

    /// <summary>
    /// Transient (SPEC-1082 D7): true when the import processor wants a stamp written for this CSO,
    /// even when both pending values above are null (Delta Import conservative nulling). Distinct
    /// from "both pending values are null" so that "no stamp requested" and "stamp requested with
    /// null values" are unambiguous.
    /// </summary>
    [NotMapped]
    public bool PendingImportStateStampRequested { get; set; }

    public ConnectedSystemObjectStatus Status { get; set; } = ConnectedSystemObjectStatus.Normal;

    /// <summary>
    /// If there's a link to a MetaverseObject here, then this is a connected object,
    /// </summary>
    public MetaverseObject? MetaverseObject { get; set; }

    /// <summary>
    /// Foreign key for the MetaverseObject navigation property
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// How was this CSO joined to an MVO, if at all?
    /// </summary>
    public ConnectedSystemObjectJoinType JoinType { get; set; } = ConnectedSystemObjectJoinType.NotJoined;

    /// <summary>
    /// When this Connected System Object was joined to the Metaverse.
    /// </summary>
    public DateTime? DateJoined { get; set; }

    /// <summary>
    /// Set by the Temporal Scope Reconciler when this object's relative-date (inbound) scope membership
    /// has flipped purely because the clock advanced, with no source-data change. The flag lets the
    /// synchronisation engine's unchanged-since-last-sync skip pass this object through for re-evaluation,
    /// then is cleared once it has been processed. Part of the flag-and-delegate model (issue #892): the
    /// reconciler only flags; the existing engine applies the correct outcome (project, join, Attribute
    /// Flow, disconnect, delete, etc.).
    /// </summary>
    public bool ScopeReviewPending { get; set; }

    /// <summary>
    /// UTC watermark of when the Temporal Scope Reconciler last evaluated this object's relative-date scope.
    /// Bounds each reconciliation sweep to the objects whose temporal boundary could have crossed since they
    /// were last evaluated. Null until first reconciled.
    /// </summary>
    public DateTime? LastScopeEvaluatedAt { get; set; }

    /// <summary>
    /// A list of the changes made to this Connected System Object.
    /// </summary>
    public List<ConnectedSystemObjectChange> Changes { get; set; } = null!;

    /// <summary>
    /// Set by the page loader when a CSO's attributes have not changed since the last completed sync.
    /// When true, the sync processor skips Attribute Flow, export evaluation, and drift detection
    /// for this CSO — avoiding the overhead of loading and comparing unchanged attribute values.
    /// </summary>
    [NotMapped]
    public bool IsUnchangedSinceLastSync { get; set; }

    /// <summary>
    /// Only for use by JIM.Service to determine what attribute values need adding and change-tracking.
    /// </summary>
    [NotMapped]
    public List<ConnectedSystemObjectAttributeValue> PendingAttributeValueAdditions { get; set; } = new();

    /// <summary>
    /// Only for use by JIM.Service to determine what attribute values need removing and change-tracking.
    /// </summary>
    [NotMapped]
    public List<ConnectedSystemObjectAttributeValue> PendingAttributeValueRemovals { get; set; } = new();

    [NotMapped]
    public ConnectedSystemObjectAttributeValue? ExternalIdAttributeValue
    {
        get
        {
            if (AttributeValues.Count == 0)
                return null;

            return AttributeValues.SingleOrDefault(q => (q.AttributeId != 0 ? q.AttributeId : q.Attribute?.Id) == ExternalIdAttributeId);
        }
    }

    [NotMapped]
    public ConnectedSystemObjectAttributeValue? SecondaryExternalIdAttributeValue
    {
        get
        {
            if (AttributeValues.Count == 0)
                return null;

            return AttributeValues.SingleOrDefault(q => (q.AttributeId != 0 ? q.AttributeId : q.Attribute?.Id) == SecondaryExternalIdAttributeId);
        }
    }

    /// <summary>
    /// The object's name as the Connected System knows it: the first present value from
    /// <see cref="ObjectNaming.ConnectedSystemNameAttributes"/>, or null when it carries none of them.
    /// <para>
    /// Use this when persisting a name alongside a separately persisted identifier (a snapshot's
    /// display-name column, a nullable API field). For anything a person reads on screen use
    /// <see cref="NameOrId"/>, which falls through to an identifier rather than returning null.
    /// </para>
    /// </summary>
    [NotMapped]
    public string? Name
    {
        get
        {
            if (AttributeValues.Count == 0)
                return null;

            return ObjectNaming.BestRanked(
                AttributeValues.Select(av => (av.Attribute?.Name, av.StringValue)),
                ObjectNaming.ConnectedSystemNameRank);
        }
    }

    /// <summary>
    /// The best human-readable label for this object: its <see cref="Name"/>, else the external id,
    /// else the secondary external id (the DN, for LDAP systems). Prefer this for display; prefer
    /// <see cref="Name"/> when the identifier is already being surfaced separately.
    /// </summary>
    [NotMapped]
    public string? NameOrId => ObjectNaming.FirstPresent(
        Name,
        ExternalIdAttributeValue?.ToStringNoName(),
        SecondaryExternalIdAttributeValue?.ToStringNoName());
    #endregion

    #region public methods
    public void UpdateSingleValuedAttribute<T>(ConnectedSystemObjectTypeAttribute connectedSystemAttribute, T newAttributeValue)
    {
        if (connectedSystemAttribute.AttributePlurality != AttributePlurality.SingleValued)
            throw new ArgumentException($"Attribute '{connectedSystemAttribute.Name}' is not a Single-Valued Attribute. Cannot update value. Use the Add/Remove Multi-Valued attribute methods instead.", nameof(connectedSystemAttribute));

        // the attribute might have pending changes already, so clear any previous pending changes as we can only
        // accept the last change to an SVA
        PendingAttributeValueAdditions.RemoveAll(q => q.Attribute.Id == connectedSystemAttribute.Id);
        PendingAttributeValueRemovals.RemoveAll(q => q.Attribute.Id == connectedSystemAttribute.Id);

        // create a new attribute value object for the addition
        var connectedSystemObjectAttributeValue = new ConnectedSystemObjectAttributeValue
        {
            Attribute = connectedSystemAttribute
        };

        // we need to cast the generic value back to object before we can cast to the specific attribute type next
        // and assign the correct attribute value.
        var newAttributeValueObject = newAttributeValue as object;
        if (typeof(T) == typeof(string))
            connectedSystemObjectAttributeValue.StringValue = newAttributeValueObject as string;
        else if (typeof(T) == typeof(int))
            connectedSystemObjectAttributeValue.IntValue = newAttributeValueObject as int?;
        else if (typeof(T) == typeof(DateTime))
            connectedSystemObjectAttributeValue.DateTimeValue = newAttributeValueObject as DateTime?;
        else if (typeof(T) == typeof(Guid))
            connectedSystemObjectAttributeValue.GuidValue = newAttributeValueObject as Guid?;
        else if (typeof(T) == typeof(bool))
            connectedSystemObjectAttributeValue.BoolValue = newAttributeValueObject as bool?;
        else if (typeof(T) == typeof(byte[]))
            connectedSystemObjectAttributeValue.ByteValue = newAttributeValueObject as byte[];
        else if (typeof(T) == typeof(ConnectedSystemObject))
            connectedSystemObjectAttributeValue.ReferenceValue = newAttributeValueObject as ConnectedSystemObject;
        else
            throw new ArgumentNullException(nameof(newAttributeValue), "New attribute value was not an accepted attribute value type!");

        // if all is good by this point, add the change attribute to the list of pending attribute changes
        PendingAttributeValueAdditions.Add(connectedSystemObjectAttributeValue);

        // add removal for the existing value
        var existingAttributeValue = AttributeValues.SingleOrDefault(av => av.Attribute.Id == connectedSystemAttribute.Id);
        if (existingAttributeValue != null)
            PendingAttributeValueRemovals.Add(existingAttributeValue);
    }

    public void RemoveSingleValuedAttributeValue<T>(ConnectedSystemObjectTypeAttribute connectedSystemAttribute)
    {
        if (connectedSystemAttribute.AttributePlurality != AttributePlurality.SingleValued)
            throw new ArgumentException($"Attribute '{connectedSystemAttribute.Name}' is not a Single-Valued attribute (SVA). Cannot update value. Use the Add/Remove Multi-Valued attribute methods instead.", nameof(connectedSystemAttribute));

        var existingAttributeValue = AttributeValues.SingleOrDefault(av => av.Attribute.Id == connectedSystemAttribute.Id);
        if (existingAttributeValue != null)
            PendingAttributeValueRemovals.Add(existingAttributeValue);
    }

    public void AddMultiValuedAttributeValue<T>(ConnectedSystemObjectTypeAttribute connectedSystemAttribute, T attributeValueToAdd)
    {
        if (connectedSystemAttribute.AttributePlurality != AttributePlurality.MultiValued)
            throw new ArgumentException($"Attribute '{connectedSystemAttribute.Name}' is not a Multi-Valued attribute (MVA). Cannot add a value. Use the UpdateSingleValuedAttribute method instead.", nameof(connectedSystemAttribute));

        // create a new attribute value object for the addition
        var connectedSystemObjectAttributeValue = new ConnectedSystemObjectAttributeValue
        {
            Attribute = connectedSystemAttribute
        };

        // we need to cast the generic value back to object before we can cast to the specific attribute type next
        // and assign the correct attribute value.
        var newAttributeValueObject = attributeValueToAdd as object;
        if (typeof(T) == typeof(string))
            connectedSystemObjectAttributeValue.StringValue = newAttributeValueObject as string;
        else if (typeof(T) == typeof(int))
            connectedSystemObjectAttributeValue.IntValue = newAttributeValueObject as int?;
        else if (typeof(T) == typeof(DateTime))
            connectedSystemObjectAttributeValue.DateTimeValue = newAttributeValueObject as DateTime?;
        else if (typeof(T) == typeof(Guid))
            connectedSystemObjectAttributeValue.GuidValue = newAttributeValueObject as Guid?;
        else if (typeof(T) == typeof(bool))
            connectedSystemObjectAttributeValue.BoolValue = newAttributeValueObject as bool?;
        else if (typeof(T) == typeof(byte[]))
            connectedSystemObjectAttributeValue.ByteValue = newAttributeValueObject as byte[];
        else if (typeof(T) == typeof(ConnectedSystemObject))
            connectedSystemObjectAttributeValue.ReferenceValue = newAttributeValueObject as ConnectedSystemObject;
        else
            throw new ArgumentNullException(nameof(attributeValueToAdd), "New attribute value was not an accepted attribute value type!");

        // if all is good by this point, add the change attribute to the list of pending attribute additions
        PendingAttributeValueAdditions.Add(connectedSystemObjectAttributeValue);
    }

    public void RemoveMultiValuedAttributeValue(ConnectedSystemObjectAttributeValue attributeValueToRemove)
    {
        if (attributeValueToRemove.Attribute.AttributePlurality != AttributePlurality.MultiValued)
            throw new ArgumentException($"Attribute '{attributeValueToRemove.Attribute.Name}' is not a Multi-Valued attribute (MVA). Cannot remove a value. Use the RemoveSingleValuedAttributeValue method instead.", nameof(attributeValueToRemove));

        // add  removal for the existing value
        var existingAttributeValue = AttributeValues.SingleOrDefault(av => av.Id == attributeValueToRemove.Id);
        if (existingAttributeValue != null)
            PendingAttributeValueRemovals.Add(existingAttributeValue);
    }

    public void RemoveAllMultiValuedAttributeValues(ConnectedSystemObjectTypeAttribute connectedSystemAttribute)
    {
        foreach (var attributeValue in AttributeValues.Where(av => av.Attribute.Id == connectedSystemAttribute.Id))
            RemoveMultiValuedAttributeValue(attributeValue);
    }

    public ConnectedSystemObjectAttributeValue? GetAttributeValue(string attributeName)
    {
        return AttributeValues.SingleOrDefault(q => q.Attribute?.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase) == true);
    }

    public List<ConnectedSystemObjectAttributeValue> GetAttributeValues(string attributeName)
    {
        return AttributeValues.Where(q => q.Attribute?.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase) == true).ToList();
    }

    public override string ToString()
    {
        return $"{NameOrId} ({Id})";
    }
    #endregion
}
