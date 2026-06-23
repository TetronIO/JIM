// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Search;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a single predefined-search criterion (one filter condition on a Metaverse attribute).
/// </summary>
public class PredefinedSearchCriteriaDto
{
    /// <summary>
    /// The unique identifier of the criterion.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The Metaverse Attribute ID being evaluated.
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Attribute being evaluated.
    /// </summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// The data type of the attribute (Text, Number, LongNumber, DateTime, Boolean, Guid).
    /// </summary>
    public string AttributeDataType { get; set; } = null!;

    /// <summary>
    /// The comparison operator (Equals, NotEquals, Contains, StartsWith, GreaterThan, etc.).
    /// </summary>
    public string ComparisonType { get; set; } = null!;

    /// <summary>
    /// The string value to compare against (for Text attributes).
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// The integer value to compare against (for Number attributes).
    /// </summary>
    public int? IntValue { get; set; }

    /// <summary>
    /// The long integer value to compare against (for LongNumber attributes).
    /// </summary>
    public long? LongValue { get; set; }

    /// <summary>
    /// The date/time value to compare against (for DateTime attributes). Stored and compared in UTC.
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>
    /// The boolean value to compare against (for Boolean attributes).
    /// </summary>
    public bool? BoolValue { get; set; }

    /// <summary>
    /// The GUID value to compare against (for Guid attributes).
    /// </summary>
    public Guid? GuidValue { get; set; }

    /// <summary>
    /// When true (default), text value comparisons are case-sensitive. Only applies to Text attributes.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// Creates a DTO from an entity. The entity's MetaverseAttribute navigation should be populated.
    /// </summary>
    public static PredefinedSearchCriteriaDto FromEntity(PredefinedSearchCriteria entity)
    {
        return new PredefinedSearchCriteriaDto
        {
            Id = entity.Id,
            MetaverseAttributeId = entity.MetaverseAttribute?.Id ?? entity.MetaverseAttributeId,
            MetaverseAttributeName = entity.MetaverseAttribute?.Name,
            AttributeDataType = entity.MetaverseAttribute?.Type.ToString() ?? "Unknown",
            ComparisonType = entity.ComparisonType.ToString(),
            StringValue = entity.StringValue,
            IntValue = entity.IntValue,
            LongValue = entity.LongValue,
            DateTimeValue = entity.DateTimeValue,
            BoolValue = entity.BoolValue,
            GuidValue = entity.GuidValue,
            CaseSensitive = entity.CaseSensitive
        };
    }
}

/// <summary>
/// API representation of a predefined-search criteria group (criteria combined with All (AND) or Any (OR) logic).
/// </summary>
public class PredefinedSearchCriteriaGroupDto
{
    /// <summary>
    /// The unique identifier of the criteria group.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The logical operator for this group: "All" (AND) or "Any" (OR).
    /// </summary>
    public string Type { get; set; } = null!;

    /// <summary>
    /// The position/order of this group relative to its siblings.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// The criteria in this group.
    /// </summary>
    public List<PredefinedSearchCriteriaDto> Criteria { get; set; } = new();

    /// <summary>
    /// Nested child groups. A group combines its criteria and child groups with AND (type All) or OR (type Any);
    /// top-level groups are combined with OR. Nesting is supported one level deep.
    /// </summary>
    public List<PredefinedSearchCriteriaGroupDto> ChildGroups { get; set; } = new();

    /// <summary>
    /// Creates a DTO from an entity.
    /// </summary>
    public static PredefinedSearchCriteriaGroupDto FromEntity(PredefinedSearchCriteriaGroup entity)
    {
        return new PredefinedSearchCriteriaGroupDto
        {
            Id = entity.Id,
            Type = entity.Type.ToString(),
            Position = entity.Position,
            Criteria = entity.Criteria.Select(PredefinedSearchCriteriaDto.FromEntity).ToList(),
            ChildGroups = entity.ChildGroups.Select(FromEntity).ToList()
        };
    }
}

/// <summary>
/// Request DTO for creating a new predefined-search criteria group.
/// </summary>
public class CreatePredefinedSearchCriteriaGroupRequest
{
    /// <summary>
    /// The logical operator for this group: "All" (AND) or "Any" (OR).
    /// </summary>
    [Required]
    public string Type { get; set; } = "All";

    /// <summary>
    /// The position/order of this group (optional, defaults to 0).
    /// </summary>
    public int Position { get; set; } = 0;
}

/// <summary>
/// Request DTO for updating a predefined-search criteria group.
/// </summary>
public class UpdatePredefinedSearchCriteriaGroupRequest
{
    /// <summary>
    /// The logical operator for this group: "All" (AND) or "Any" (OR).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// The position/order of this group.
    /// </summary>
    public int? Position { get; set; }
}

/// <summary>
/// Request DTO for creating or fully updating a predefined-search criterion.
/// Provide the value carrier that matches the attribute's data type (e.g. IntValue for a Number attribute).
/// </summary>
public class PredefinedSearchCriterionRequest
{
    /// <summary>
    /// The Metaverse Attribute ID to evaluate. Must belong to the predefined search's Metaverse Object Type.
    /// </summary>
    [Required]
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The comparison operator: Equals, NotEquals, Contains, NotContains, StartsWith, NotStartsWith,
    /// EndsWith, NotEndsWith (Text only); LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals
    /// (Number, LongNumber, DateTime); Equals, NotEquals (Boolean, Guid).
    /// </summary>
    [Required]
    public string ComparisonType { get; set; } = null!;

    /// <summary>
    /// The string value to compare against (for Text attributes).
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// The integer value to compare against (for Number attributes).
    /// </summary>
    public int? IntValue { get; set; }

    /// <summary>
    /// The long integer value to compare against (for LongNumber attributes).
    /// </summary>
    public long? LongValue { get; set; }

    /// <summary>
    /// The date/time value to compare against (for DateTime attributes). Interpreted as UTC.
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>
    /// The boolean value to compare against (for Boolean attributes).
    /// </summary>
    public bool? BoolValue { get; set; }

    /// <summary>
    /// The GUID value to compare against (for Guid attributes).
    /// </summary>
    public Guid? GuidValue { get; set; }

    /// <summary>
    /// When true (default), text value comparisons are case-sensitive. Only applies to Text attributes.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;
}
