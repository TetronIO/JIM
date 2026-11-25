// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// The values to remove from an attribute in Accumulate mode. Name each value by its id, by its text, or both.
/// </summary>
public class RemoveMetaverseObjectAttributeValuesRequest
{
    /// <summary>
    /// The values to remove, by the value id shown on each of the Metaverse Object's attribute values.
    /// </summary>
    public List<Guid>? ValueIds { get; set; }

    /// <summary>
    /// The values to remove, by their text: text as it is (matched exactly, including case), numbers in invariant form,
    /// dates as ISO 8601, a Guid in its hyphenated form, a Boolean as <c>True</c> or <c>False</c>, binary as Base64.
    /// </summary>
    public List<string>? Values { get; set; }
}

/// <summary>
/// The values removed, and the Activity recording the removal.
/// </summary>
public class RemoveMetaverseObjectAttributeValuesResponse
{
    /// <summary>
    /// The Activity recording the removal.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The values removed, as text.
    /// </summary>
    public List<string> RemovedValues { get; set; } = [];

    /// <summary>
    /// The removed values, each with the Synchronisation Rule and Connected System that contributed it, as JIM held it.
    /// </summary>
    public List<RemovedAttributeValueDto> Removed { get; set; } = [];

    /// <summary>Builds the response from the result of a removal.</summary>
    public static RemoveMetaverseObjectAttributeValuesResponse FromResult(AttributeValueRemovalResult result) => new()
    {
        ActivityId = result.ActivityId ?? Guid.Empty,
        RemovedValues = result.RemovedValues.Select(MetaverseAttributeValueText.Of).OfType<string>().ToList(),
        Removed = result.RemovedValues.Select(v => new RemovedAttributeValueDto
        {
            Id = v.Id,
            Value = MetaverseAttributeValueText.Of(v),
            ContributedBySyncRuleId = v.ContributedBySyncRuleId,
            ContributedBySystemId = v.ContributedBySystemId
        }).ToList()
    };
}

/// <summary>
/// One value removed from an attribute.
/// </summary>
public class RemovedAttributeValueDto
{
    /// <summary>The value's id.</summary>
    public Guid Id { get; set; }

    /// <summary>The value, as text.</summary>
    public string? Value { get; set; }

    /// <summary>The Synchronisation Rule that contributed the value; null when it has since been deleted, or for a value no rule contributed.</summary>
    public int? ContributedBySyncRuleId { get; set; }

    /// <summary>The Connected System that contributed the value; null for a value no Connected System contributed.</summary>
    public int? ContributedBySystemId { get; set; }
}
