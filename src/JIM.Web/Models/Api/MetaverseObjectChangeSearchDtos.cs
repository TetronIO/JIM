// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.History;

namespace JIM.Web.Models.Api;

/// <summary>
/// A search for changes to Metaverse Objects across every object. Every criterion is optional.
/// </summary>
public class MetaverseObjectChangeSearchRequest
{
    /// <summary>
    /// Only changes made on or after this time (UTC).
    /// </summary>
    public DateTime? FromDate { get; set; }

    /// <summary>
    /// Only changes made on or before this time (UTC).
    /// </summary>
    public DateTime? ToDate { get; set; }

    /// <summary>
    /// Only changes to Metaverse Objects of this Metaverse Object Type, including objects since deleted.
    /// </summary>
    public int? ObjectTypeId { get; set; }

    /// <summary>
    /// Only changes to Metaverse Objects whose display name contains this text, case-insensitively.
    /// </summary>
    public string? DisplayNameSearch { get; set; }

    /// <summary>
    /// How the attribute changed. At most ten conditions.
    /// </summary>
    public List<MetaverseObjectChangeConditionRequest> Conditions { get; set; } = [];

    /// <summary>
    /// <c>All</c>: every condition must be met by the same change, to one Metaverse Object in one Activity.
    /// <c>Any</c>: one condition is enough.
    /// </summary>
    public AttributeConditionMatchMode MatchMode { get; set; } = AttributeConditionMatchMode.All;

    /// <summary>
    /// The page of results to return, from 1.
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// How many changes to return per page, up to 100.
    /// </summary>
    public int PageSize { get; set; } = 25;
}

/// <summary>
/// One attribute condition in a Metaverse Object change search.
/// </summary>
public class MetaverseObjectChangeConditionRequest
{
    /// <summary>
    /// The Metaverse Attribute's id.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// <c>ChangedTo</c>: a value was added (a single-valued attribute changed to it). <c>ChangedFrom</c>: a value was
    /// removed. <c>AnyChange</c>: either.
    /// </summary>
    public AttributeChangeOperator Operator { get; set; } = AttributeChangeOperator.AnyChange;

    /// <summary>
    /// The value to match, or empty for any value. It must be of the attribute's data type: a date is
    /// <c>yyyy-MM-dd</c> (the whole day, UTC) or a date and time. Text and Reference values match case-insensitively,
    /// a Reference value against the referenced Metaverse Object's display name, and <c>*</c> matches any run of
    /// characters.
    /// </summary>
    public string? Value { get; set; }
}

/// <summary>
/// A change to one Metaverse Object found by a change search.
/// </summary>
public class MetaverseObjectChangeSearchItem
{
    /// <summary>
    /// The change's id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// When the change was made (UTC).
    /// </summary>
    public DateTime ChangeTime { get; set; }

    /// <summary>
    /// What kind of change it was.
    /// </summary>
    public ObjectChangeType ChangeType { get; set; }

    /// <summary>
    /// The changed Metaverse Object's id. For an object since deleted, the id it had. Empty when the change record
    /// predates the object's id being preserved.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// Whether the Metaverse Object has since been deleted.
    /// </summary>
    public bool MetaverseObjectDeleted { get; set; }

    /// <summary>
    /// When the Metaverse Object was deleted. Empty unless it has since been deleted.
    /// </summary>
    public DateTime? MetaverseObjectDeletedAt { get; set; }

    /// <summary>
    /// The Metaverse Object's display name, or as it was when it was deleted. Empty when it has none.
    /// </summary>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// The Metaverse Object Type's id. Empty when it cannot be known.
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The Metaverse Object Type's name. Empty when it cannot be known.
    /// </summary>
    public string? MetaverseObjectTypeName { get; set; }

    /// <summary>
    /// The kind of principal that made the change.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The name of the principal that made the change, as it was at the time.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// What within JIM made the change: a Synchronisation Rule, a User, Example Data or System, for example.
    /// </summary>
    public MetaverseObjectChangeInitiatorType ChangeInitiatorType { get; set; }

    /// <summary>
    /// The Synchronisation Rule that made the change, as it was named at the time. Empty for a change not made by a
    /// synchronisation.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The Activity the change was made in, such as a synchronisation, a scheduled deletion or a sign-in. Empty when
    /// history retention has since removed the Activity, or for a change recorded before Activities were linked to
    /// changes.
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// The attributes that changed, with the values added and removed.
    /// </summary>
    public List<MetaverseObjectChangeSearchAttributeItem> AttributeChanges { get; set; } = [];

    /// <summary>
    /// Builds the response item from a search result.
    /// </summary>
    public static MetaverseObjectChangeSearchItem FromResult(MetaverseObjectChangeSearchResult result) => new()
    {
        Id = result.Change.Id,
        ChangeTime = result.Change.ChangeTime,
        ChangeType = result.Change.ChangeType,
        MetaverseObjectId = result.MetaverseObjectId,
        MetaverseObjectDeleted = result.MetaverseObjectDeleted,
        MetaverseObjectDeletedAt = result.MetaverseObjectDeletedAt,
        MetaverseObjectDisplayName = result.MetaverseObjectDisplayName,
        MetaverseObjectTypeId = result.MetaverseObjectTypeId,
        MetaverseObjectTypeName = result.MetaverseObjectTypeName,
        InitiatedByType = result.Change.InitiatedByType,
        InitiatedByName = result.Change.InitiatedByName,
        ChangeInitiatorType = result.Change.ChangeInitiatorType,
        SyncRuleName = result.Change.SyncRuleName,
        ActivityId = result.Change.ActivityId,
        AttributeChanges = result.Change.AttributeChanges
            .Select(ac => new MetaverseObjectChangeSearchAttributeItem
            {
                AttributeId = ac.AttributeId,
                AttributeName = ac.AttributeName,
                Matched = ac.AttributeId.HasValue && result.MatchedAttributeIds.Contains(ac.AttributeId.Value),
                AddedValues = ValueTexts(ac, ValueChangeType.Add),
                RemovedValues = ValueTexts(ac, ValueChangeType.Remove),
                ValueChangeTotal = ac.ValueChangeTotal
            })
            .ToList()
    };

    private static List<string> ValueTexts(MvoAttributeChangeDto attributeChange, ValueChangeType valueChangeType) =>
        attributeChange.ValueChanges
            .Where(v => v.ValueChangeType == valueChangeType)
            .Select(v => v.ReferenceValue?.DisplayName ?? v.ToDisplayString())
            .ToList();
}

/// <summary>
/// An attribute changed by a Metaverse Object change.
/// </summary>
public class MetaverseObjectChangeSearchAttributeItem
{
    /// <summary>
    /// The Metaverse Attribute's id. Empty when the attribute has since been deleted.
    /// </summary>
    public int? AttributeId { get; set; }

    /// <summary>
    /// The attribute's name, as it was when the change was made.
    /// </summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// Whether one of the search's conditions named this attribute.
    /// </summary>
    public bool Matched { get; set; }

    /// <summary>
    /// The values added, as text, among the attribute's first ten value changes. A reference is the referenced
    /// Metaverse Object's display name, or its id when it has none.
    /// </summary>
    public List<string> AddedValues { get; set; } = [];

    /// <summary>
    /// The values removed, as text, among the attribute's first ten value changes.
    /// </summary>
    public List<string> RemovedValues { get; set; } = [];

    /// <summary>
    /// How many value changes the attribute has in all. When it is more than ten, the rest are read a page at a time
    /// from the change's attribute values endpoint.
    /// </summary>
    public int ValueChangeTotal { get; set; }
}
