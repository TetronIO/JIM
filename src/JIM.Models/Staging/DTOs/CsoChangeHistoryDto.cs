// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One change to a Connected System Object: when it happened, who or what made it, and what it changed.
/// </summary>
public class CsoChangeHistoryDto
{
    /// <summary>
    /// The ID of the change.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// What kind of change it was.
    /// </summary>
    public ObjectChangeType ChangeType { get; set; }

    /// <summary>
    /// When the change was made (UTC).
    /// </summary>
    public DateTime ChangeTime { get; set; }

    /// <summary>
    /// What made the change.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The ID of the user or API key that made the change; null when the System did.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The name of whoever made the change.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// The ID of the Run Profile execution item that recorded the change, when a Run Profile made it; null
    /// otherwise.
    /// </summary>
    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// The name of the Run Profile that made the change; null when none did.
    /// </summary>
    public string? RunProfileName { get; set; }

    /// <summary>
    /// The attributes the change touched, each with the values it added and removed.
    /// </summary>
    public List<CsoAttributeChangeDto> AttributeChanges { get; set; } = new();
}

/// <summary>
/// Per-attribute change within a <see cref="CsoChangeHistoryDto"/>.
/// </summary>
public class CsoAttributeChangeDto
{
    public string AttributeName { get; set; } = string.Empty;

    public AttributeDataType AttributeType { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public List<CsoValueChangeDto> ValueChanges { get; set; } = new();
}

/// <summary>
/// Per-value change within a <see cref="CsoAttributeChangeDto"/>. Holds the typed scalar value
/// or, for reference attributes, a flattened reference target.
/// </summary>
public class CsoValueChangeDto
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

    public CsoChangeReferenceDto? ReferenceValue { get; set; }

    /// <summary>
    /// The export Synchronisation Rule whose mapping produced this value. Null when the value was not
    /// produced by an export mapping (e.g. import-side changes carry no rule attribution), or the
    /// contributing rule has since been deleted.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the contributing Synchronisation Rule's name, surviving deletion of the rule.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// Returns the human-readable representation of the value, mirroring the behaviour of
    /// <c>ConnectedSystemObjectChangeAttributeValue.ToStringNoName()</c> so the UI does not
    /// need access to the original entity.
    /// </summary>
    public string ToDisplayString()
    {
        if (!string.IsNullOrEmpty(StringValue))
            return StringValue;

        if (DateTimeValue.HasValue)
            return DateTimeValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

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
/// Flattened reference value for a CSO change. Carries the connected-system context so the UI
/// can build an href, plus the best-effort display name and secondary id projected from the
/// reference target's attribute values (no full entity materialisation).
/// </summary>
public class CsoChangeReferenceDto
{
    public Guid Id { get; set; }

    public int ConnectedSystemId { get; set; }

    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Best-effort display label sourced from the reference target's <c>displayName</c> attribute,
    /// falling back to its external id, then secondary external id. Null if none were resolvable.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Reference target's secondary external id (e.g. DN for LDAP systems). Surfaced alongside
    /// the display name in the timeline UI.
    /// </summary>
    public string? SecondaryId { get; set; }
}
