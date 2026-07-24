// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;
using JIM.Models.Search;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a Synchronisation Rule scoping criteria (a single filter condition).
/// </summary>
public class SyncRuleScopingCriteriaDto
{
    /// <summary>
    /// The unique identifier of the scoping criteria.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The Metaverse Attribute ID being evaluated (for export Synchronisation Rules).
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Attribute being evaluated (for export Synchronisation Rules).
    /// </summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// The Connected System Attribute ID being evaluated (for import Synchronisation Rules).
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The name of the Connected System Attribute being evaluated (for import Synchronisation Rules).
    /// </summary>
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// The data type of the attribute.
    /// </summary>
    public string AttributeDataType { get; set; } = null!;

    /// <summary>
    /// The comparison operator (Equals, NotEquals, Contains, StartsWith, etc.).
    /// </summary>
    public string ComparisonType { get; set; } = null!;

    /// <summary>
    /// The string value to compare against (for text attributes).
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// The integer value to compare against (for number attributes).
    /// </summary>
    public int? IntValue { get; set; }

    /// <summary>
    /// The long integer value to compare against (for long number attributes).
    /// </summary>
    public long? LongValue { get; set; }

    /// <summary>
    /// The decimal value to compare against (for decimal attributes).
    /// </summary>
    public decimal? DecimalValue { get; set; }

    /// <summary>
    /// The date/time value to compare against (for datetime attributes).
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>
    /// The boolean value to compare against (for boolean attributes).
    /// </summary>
    public bool? BoolValue { get; set; }

    /// <summary>
    /// The GUID value to compare against (for GUID attributes).
    /// </summary>
    public Guid? GuidValue { get; set; }

    /// <summary>
    /// When true (default), value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// Only applies to text/string comparisons.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// For DateTime attributes, whether the criterion compares against a fixed date ("Absolute", the default)
    /// or a date resolved relative to now ("Relative").
    /// </summary>
    public string ValueMode { get; set; } = nameof(DateCriteriaValueMode.Absolute);

    /// <summary>
    /// The relative offset count (when ValueMode is Relative).
    /// </summary>
    public int? RelativeCount { get; set; }

    /// <summary>
    /// The relative offset unit: Hours, Days, Weeks, Months, Years (when ValueMode is Relative).
    /// </summary>
    public string? RelativeUnit { get; set; }

    /// <summary>
    /// The relative offset direction: Ago or FromNow (when ValueMode is Relative).
    /// </summary>
    public string? RelativeDirection { get; set; }

    /// <summary>
    /// Creates a DTO from an entity.
    /// </summary>
    public static SyncRuleScopingCriteriaDto FromEntity(SyncRuleScopingCriteria entity)
    {
        var dto = new SyncRuleScopingCriteriaDto
        {
            Id = entity.Id,
            ComparisonType = entity.ComparisonType.ToString(),
            StringValue = entity.StringValue,
            IntValue = entity.IntValue,
            LongValue = entity.LongValue,
            DecimalValue = entity.DecimalValue,
            DateTimeValue = entity.DateTimeValue,
            BoolValue = entity.BoolValue,
            GuidValue = entity.GuidValue,
            CaseSensitive = entity.CaseSensitive,
            ValueMode = entity.ValueMode.ToString(),
            RelativeCount = entity.RelativeCount,
            RelativeUnit = entity.RelativeUnit?.ToString(),
            RelativeDirection = entity.RelativeDirection?.ToString()
        };

        // Set attribute info based on which one is set
        if (entity.MetaverseAttribute != null)
        {
            dto.MetaverseAttributeId = entity.MetaverseAttribute.Id;
            dto.MetaverseAttributeName = entity.MetaverseAttribute.Name;
            dto.AttributeDataType = entity.MetaverseAttribute.Type.ToString();
        }
        else if (entity.ConnectedSystemAttribute != null)
        {
            dto.ConnectedSystemAttributeId = entity.ConnectedSystemAttribute.Id;
            dto.ConnectedSystemAttributeName = entity.ConnectedSystemAttribute.Name;
            dto.AttributeDataType = entity.ConnectedSystemAttribute.Type.ToString();
        }
        else
        {
            dto.AttributeDataType = "Unknown";
        }

        return dto;
    }
}

/// <summary>
/// API representation of a Synchronisation Rule scoping criteria group (contains criteria with AND/OR logic).
/// </summary>
public class SyncRuleScopingCriteriaGroupDto
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
    /// The position/order of this group (for ordering at same level).
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// The criteria in this group.
    /// </summary>
    public List<SyncRuleScopingCriteriaDto> Criteria { get; set; } = new();

    /// <summary>
    /// Nested child groups (for complex logical expressions).
    /// </summary>
    public List<SyncRuleScopingCriteriaGroupDto> ChildGroups { get; set; } = new();

    /// <summary>
    /// Creates a DTO from an entity.
    /// </summary>
    public static SyncRuleScopingCriteriaGroupDto FromEntity(SyncRuleScopingCriteriaGroup entity)
    {
        return new SyncRuleScopingCriteriaGroupDto
        {
            Id = entity.Id,
            Type = entity.Type.ToString(),
            Position = entity.Position,
            Criteria = entity.Criteria.Select(SyncRuleScopingCriteriaDto.FromEntity).ToList(),
            ChildGroups = entity.ChildGroups.Select(FromEntity).ToList()
        };
    }
}

/// <summary>
/// Request DTO for creating a new scoping criteria group.
/// </summary>
public class CreateScopingCriteriaGroupRequest
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
/// Request DTO for updating a scoping criteria group.
/// </summary>
public class UpdateScopingCriteriaGroupRequest
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
/// Request DTO for creating a new scoping criterion within a group.
/// For export Synchronisation Rules, provide MetaverseAttributeId.
/// For import Synchronisation Rules, provide ConnectedSystemAttributeId.
/// </summary>
public class CreateScopingCriterionRequest
{
    /// <summary>
    /// The Metaverse Attribute ID to evaluate (for export Synchronisation Rules).
    /// Either MetaverseAttributeId or ConnectedSystemAttributeId must be provided.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The Connected System Attribute ID to evaluate (for import Synchronisation Rules).
    /// Either MetaverseAttributeId or ConnectedSystemAttributeId must be provided.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The comparison operator: Equals, NotEquals, Contains, StartsWith, EndsWith,
    /// NotContains, NotStartsWith, NotEndsWith, LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals.
    /// </summary>
    [Required]
    public string ComparisonType { get; set; } = null!;

    /// <summary>
    /// The string value to compare against (for text attributes).
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// The integer value to compare against (for number attributes).
    /// </summary>
    public int? IntValue { get; set; }

    /// <summary>
    /// The long integer value to compare against (for long number attributes).
    /// </summary>
    public long? LongValue { get; set; }

    /// <summary>
    /// The decimal value to compare against (for decimal attributes).
    /// </summary>
    public decimal? DecimalValue { get; set; }

    /// <summary>
    /// The date/time value to compare against (for datetime attributes).
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>
    /// The boolean value to compare against (for boolean attributes).
    /// </summary>
    public bool? BoolValue { get; set; }

    /// <summary>
    /// The GUID value to compare against (for GUID attributes).
    /// </summary>
    public Guid? GuidValue { get; set; }

    /// <summary>
    /// When true (default), value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// Only applies to text/string comparisons.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// For DateTime attributes, whether the criterion compares against a fixed date ("Absolute", the default)
    /// or a date resolved relative to now ("Relative"). When Relative, supply RelativeCount/RelativeUnit/RelativeDirection
    /// instead of DateTimeValue.
    /// </summary>
    public string? ValueMode { get; set; }

    /// <summary>
    /// The relative offset count, zero or positive (required when ValueMode is Relative).
    /// </summary>
    public int? RelativeCount { get; set; }

    /// <summary>
    /// The relative offset unit: Hours, Days, Weeks, Months, Years (required when ValueMode is Relative).
    /// </summary>
    public string? RelativeUnit { get; set; }

    /// <summary>
    /// The relative offset direction: Ago or FromNow (required when ValueMode is Relative).
    /// </summary>
    public string? RelativeDirection { get; set; }
}
