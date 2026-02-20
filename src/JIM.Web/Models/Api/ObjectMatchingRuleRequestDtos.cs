using System.ComponentModel.DataAnnotations;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating an object matching rule source.
/// </summary>
public class CreateObjectMatchingRuleSourceRequest
{
    /// <summary>
    /// The order of this source in the rule (for function chaining). Defaults to 0.
    /// </summary>
    public int Order { get; set; } = 0;

    /// <summary>
    /// The Connected System attribute ID (for import matching).
    /// Either this or MetaverseAttributeId must be specified.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The Metaverse attribute ID (for export matching).
    /// Either this or ConnectedSystemAttributeId must be specified.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }
}

/// <summary>
/// Request DTO for creating an object matching rule.
/// </summary>
public class CreateObjectMatchingRuleRequest
{
    /// <summary>
    /// The evaluation order for this rule (lower values are evaluated first).
    /// If not specified, will be assigned automatically.
    /// </summary>
    public int? Order { get; set; }

    /// <summary>
    /// The Connected System Object Type this rule belongs to.
    /// </summary>
    [Required]
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The target Metaverse attribute ID to match against.
    /// </summary>
    [Required]
    public int TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// The sources for this matching rule.
    /// At least one source must be specified.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one source must be specified.")]
    public List<CreateObjectMatchingRuleSourceRequest> Sources { get; set; } = new();

    /// <summary>
    /// When true (default), attribute value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;
}

/// <summary>
/// Request DTO for updating an object matching rule.
/// </summary>
public class UpdateObjectMatchingRuleRequest
{
    /// <summary>
    /// The new evaluation order for this rule.
    /// </summary>
    public int? Order { get; set; }

    /// <summary>
    /// The new target Metaverse attribute ID.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// If specified, replaces all sources with these new sources.
    /// </summary>
    public List<CreateObjectMatchingRuleSourceRequest>? Sources { get; set; }

    /// <summary>
    /// When true, attribute value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// </summary>
    public bool? CaseSensitive { get; set; }
}
