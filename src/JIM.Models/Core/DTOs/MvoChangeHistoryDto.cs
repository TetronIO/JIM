// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One change to a Metaverse Object: what changed, when, who or what made it, and the value changes it made to each
/// attribute.
/// </summary>
public class MvoChangeHistoryDto
{
    /// <summary>
    /// The unique identifier of the change.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// What the change did to the Metaverse Object.
    /// </summary>
    public ObjectChangeType ChangeType { get; set; }

    /// <summary>
    /// When the change was made (UTC).
    /// </summary>
    public DateTime ChangeTime { get; set; }

    /// <summary>
    /// The kind of principal that made the change: a User, an API key, or JIM itself (System).
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that made the change; null for System.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The name of the principal that made the change, as it was then.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// What within JIM made the change, such as a Synchronisation Rule, a User or Example Data.
    /// </summary>
    public MetaverseObjectChangeInitiatorType ChangeInitiatorType { get; set; }

    /// <summary>
    /// The unique identifier of the Synchronisation Rule that made the change, when one did.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// That Synchronisation Rule's name.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The Activity execution item that recorded the change, when a Run Profile made it.
    /// </summary>
    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// The Activity the change was made in, such as a synchronisation, a scheduled deletion or a sign-in. Null when
    /// the Activity has since been removed by history retention, or for a change recorded before Activities were
    /// linked to changes.
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// The unique identifier of the Connected System Object whose synchronisation made the change, when one did.
    /// </summary>
    public Guid? CsoId { get; set; }

    /// <summary>
    /// That Connected System Object's external id, as it was then; kept after the object is deleted.
    /// </summary>
    public string? CsoExternalId { get; set; }

    /// <summary>
    /// The unique identifier of the Connected System the Run Profile belonged to, when one made the change.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// That Connected System's name.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// The name of the Run Profile that made the change, when one did.
    /// </summary>
    public string? RunProfileName { get; set; }

    /// <summary>
    /// The kind of Run Profile that made the change, when one did.
    /// </summary>
    public ConnectedSystemRunType? ConnectedSystemRunType { get; set; }

    /// <summary>
    /// The attributes the change touched, each with the values it added and removed.
    /// </summary>
    public List<MvoAttributeChangeDto> AttributeChanges { get; set; } = new();
}

/// <summary>
/// Per-attribute change within an <see cref="MvoChangeHistoryDto"/>.
/// </summary>
public class MvoAttributeChangeDto
{
    public string AttributeName { get; set; } = string.Empty;

    public AttributeDataType AttributeType { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    /// <summary>
    /// The Metaverse Attribute's id, so the Changes tab can link a row into that attribute's Inspect view
    /// (#399). Null when the attribute definition has since been deleted.
    /// </summary>
    public int? AttributeId { get; set; }

    /// <summary>
    /// The attribute's first value changes, at most ten. When <c>valueChangeTotal</c> is larger, the rest are read a
    /// page at a time from the change's attribute values endpoint.
    /// </summary>
    public List<MvoValueChangeDto> ValueChanges { get; set; } = new();

    /// <summary>
    /// How many value changes the attribute has in all, including any not returned in <c>valueChanges</c>.
    /// </summary>
    public int ValueChangeTotal { get; set; }
}

/// <summary>
/// Per-value change within an <see cref="MvoAttributeChangeDto"/>. Holds the typed
/// scalar value or, for reference attributes, a flattened reference target.
/// </summary>
public class MvoValueChangeDto
{
    public ValueChangeType ValueChangeType { get; set; }

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    public long? LongValue { get; set; }

    public decimal? DecimalValue { get; set; }

    public int? ByteValueLength { get; set; }

    public Guid? GuidValue { get; set; }

    public bool? BoolValue { get; set; }

    public MvoChangeReferenceDto? ReferenceValue { get; set; }

    /// <summary>
    /// The Synchronisation Rule that contributed this value, distinct from the parent
    /// <see cref="MvoChangeHistoryDto.SyncRuleId"/> (the rule that caused the overall change): a single sync
    /// run can flow attributes from several rules onto one Metaverse Object. Null when the value was not
    /// contributed by a Synchronisation Rule, or the contributing rule has since been deleted.
    /// </summary>
    public int? ContributedBySyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the contributing Synchronisation Rule's name, surviving deletion of the rule.
    /// </summary>
    public string? ContributedBySyncRuleName { get; set; }

    /// <summary>
    /// The contributing Connected System (#399), resolved from <see cref="ContributedBySyncRuleId"/> through the
    /// still-live Synchronisation Rule. Null when the rule has since been deleted (the row's rule id is nulled at
    /// that point, so the system can no longer be resolved); <see cref="ContributedBySyncRuleName"/>'s snapshot
    /// survives regardless.
    /// </summary>
    public int? ContributedBySystemId { get; set; }

    public string? ContributedBySystemName { get; set; }

    /// <summary>
    /// What kind of source set the value: SynchronisationRule, SignInClaim, System or Person. Null when no source was
    /// recorded for the change.
    /// </summary>
    /// <remarks>#2472.</remarks>
    public ValueContributorKind? ContributedByKind { get; set; }

    /// <summary>The sign-in claim that supplied the value, such as <c>given_name</c>, when the source is a sign-in claim; otherwise null.</summary>
    public string? ContributedByClaimType { get; set; }

    /// <summary>
    /// True when a Generated Value Attribute Flow produced this value (#399): the contributing Synchronisation Rule
    /// is the generating one and the value is one the generation produced. Set by
    /// <c>MetaverseServer.GetMvoChangeHistoryAsync</c>, not by the repository.
    /// </summary>
    public bool IsGeneratedValue { get; set; }

    /// <summary>
    /// Returns the human-readable representation of the value, mirroring the
    /// behaviour of <c>MetaverseObjectChangeAttributeValue.ToString()</c> so
    /// the UI does not need access to the original entity.
    /// </summary>
    public string ToDisplayString()
    {
        if (!string.IsNullOrEmpty(StringValue))
            return StringValue;

        if (DateTimeValue.HasValue)
            return JIM.Models.Utility.AttributeDateDisplay.Format(DateTimeValue.Value);

        if (IntValue.HasValue)
            return IntValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (LongValue.HasValue)
            return LongValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (DecimalValue.HasValue)
            return DecimalValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (ByteValueLength.HasValue)
            return ByteValueLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (GuidValue.HasValue)
            return GuidValue.Value.ToString();

        if (BoolValue.HasValue)
            return BoolValue.Value.ToString();

        if (ReferenceValue != null)
            return ReferenceValue.Id.ToString();

        return string.Empty;
    }
}

/// <summary>
/// Flattened reference value for a value change. Avoids materialising the full
/// referenced <c>MetaverseObject</c> entity by carrying the display name (sourced
/// from <c>MetaverseObject.CachedDisplayName</c>) and type names directly.
/// </summary>
public class MvoChangeReferenceDto
{
    public Guid Id { get; set; }

    public string? DisplayName { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public string TypePluralName { get; set; } = string.Empty;
}
