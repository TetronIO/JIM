// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;
using JIM.Models.Search;

namespace JIM.Web.Models.Api;

/// <summary>
/// One scoping criterion: an attribute compared with a value.
/// </summary>
public class SyncRuleScopingCriteriaDto
{
    /// <summary>
    /// The ID of the criterion.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The ID of the Metaverse attribute the criterion tests, on an export Synchronisation Rule's criterion; null
    /// on an import one.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse attribute the criterion tests; null on an import Synchronisation Rule's criterion.
    /// </summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// The ID of the Connected System attribute the criterion tests, on an import Synchronisation Rule's criterion;
    /// null on an export one.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The name of the Connected System attribute the criterion tests; null on an export Synchronisation Rule's
    /// criterion.
    /// </summary>
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// The tested attribute's data type, which decides which value property holds the value.
    /// </summary>
    public string AttributeDataType { get; set; } = null!;

    /// <summary>
    /// How the attribute is compared with the value: Equals, NotEquals, StartsWith, NotStartsWith, EndsWith,
    /// NotEndsWith, Contains, NotContains, LessThan, LessThanOrEquals, GreaterThan or GreaterThanOrEquals.
    /// </summary>
    public string ComparisonType { get; set; } = null!;

    /// <summary>
    /// The value a Text attribute is compared with.
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// The value a Number attribute is compared with.
    /// </summary>
    public int? IntValue { get; set; }

    /// <summary>
    /// The value a LongNumber attribute is compared with.
    /// </summary>
    public long? LongValue { get; set; }

    /// <summary>
    /// The value a Decimal attribute is compared with.
    /// </summary>
    public decimal? DecimalValue { get; set; }

    /// <summary>
    /// The date and time a DateTime attribute is compared with, when ValueMode is Absolute.
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>
    /// The value a Boolean attribute is compared with.
    /// </summary>
    public bool? BoolValue { get; set; }

    /// <summary>
    /// The value a Guid attribute is compared with.
    /// </summary>
    public Guid? GuidValue { get; set; }

    /// <summary>
    /// Whether a Text comparison respects case; false ignores it.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// For a DateTime attribute, Absolute when it is compared with DateTimeValue, or Relative when it is compared
    /// with a date worked out from now each time the criterion is evaluated.
    /// </summary>
    public string ValueMode { get; set; } = nameof(DateCriteriaValueMode.Absolute);

    /// <summary>
    /// How many RelativeUnit from now the date is, when ValueMode is Relative; null otherwise.
    /// </summary>
    public int? RelativeCount { get; set; }

    /// <summary>
    /// The unit RelativeCount counts (Hours, Days, Weeks, Months or Years), when ValueMode is Relative; null
    /// otherwise.
    /// </summary>
    public string? RelativeUnit { get; set; }

    /// <summary>
    /// Ago for a date in the past, FromNow for one in the future, when ValueMode is Relative; null otherwise.
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
/// A scoping criteria group: criteria and child groups combined by All or Any.
/// </summary>
public class SyncRuleScopingCriteriaGroupDto
{
    /// <summary>
    /// The ID of the group.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// How the group combines what it contains: All is met when every criterion and child group in it is met, Any
    /// when at least one is.
    /// </summary>
    public string Type { get; set; } = null!;

    /// <summary>
    /// Where the group sits among the groups beside it, used to order them; it does not change which objects are in
    /// scope.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// The criteria in the group.
    /// </summary>
    public List<SyncRuleScopingCriteriaDto> Criteria { get; set; } = new();

    /// <summary>
    /// The groups inside this one, each with what it contains.
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
    /// Whether text comparisons respect case. True compares exactly; false ignores case differences. Only applies to
    /// text comparisons. When omitted, a new criterion respects case and an updated one keeps its current setting.
    /// </summary>
    public bool? CaseSensitive { get; set; }

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
