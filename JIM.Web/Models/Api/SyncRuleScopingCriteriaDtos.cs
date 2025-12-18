using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;
using JIM.Models.Search;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a sync rule scoping criteria (a single filter condition).
/// </summary>
public class SyncRuleScopingCriteriaDto
{
    /// <summary>
    /// The unique identifier of the scoping criteria.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The Metaverse Attribute ID being evaluated.
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Attribute being evaluated.
    /// </summary>
    public string MetaverseAttributeName { get; set; } = null!;

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
    /// Creates a DTO from an entity.
    /// </summary>
    public static SyncRuleScopingCriteriaDto FromEntity(SyncRuleScopingCriteria entity)
    {
        return new SyncRuleScopingCriteriaDto
        {
            Id = entity.Id,
            MetaverseAttributeId = entity.MetaverseAttribute.Id,
            MetaverseAttributeName = entity.MetaverseAttribute.Name,
            AttributeDataType = entity.MetaverseAttribute.Type.ToString(),
            ComparisonType = entity.ComparisonType.ToString(),
            StringValue = entity.StringValue,
            IntValue = entity.IntValue,
            DateTimeValue = entity.DateTimeValue,
            BoolValue = entity.BoolValue,
            GuidValue = entity.GuidValue
        };
    }
}

/// <summary>
/// API representation of a sync rule scoping criteria group (contains criteria with AND/OR logic).
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
/// </summary>
public class CreateScopingCriterionRequest
{
    /// <summary>
    /// The Metaverse Attribute ID to evaluate.
    /// </summary>
    [Required]
    public int MetaverseAttributeId { get; set; }

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
}
