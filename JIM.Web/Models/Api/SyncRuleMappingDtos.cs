using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a SyncRuleMapping.
/// </summary>
public class SyncRuleMappingDto
{
    public int Id { get; set; }
    public DateTime Created { get; set; }
    public int? TargetMetaverseAttributeId { get; set; }
    public string? TargetMetaverseAttributeName { get; set; }
    public int? TargetConnectedSystemAttributeId { get; set; }
    public string? TargetConnectedSystemAttributeName { get; set; }
    public string SourceType { get; set; } = null!;
    public List<SyncRuleMappingSourceDto> Sources { get; set; } = new();

    public static SyncRuleMappingDto FromEntity(SyncRuleMapping entity)
    {
        return new SyncRuleMappingDto
        {
            Id = entity.Id,
            Created = entity.Created,
            TargetMetaverseAttributeId = entity.TargetMetaverseAttributeId,
            TargetMetaverseAttributeName = entity.TargetMetaverseAttribute?.Name,
            TargetConnectedSystemAttributeId = entity.TargetConnectedSystemAttributeId,
            TargetConnectedSystemAttributeName = entity.TargetConnectedSystemAttribute?.Name,
            SourceType = entity.GetSourceType().ToString(),
            Sources = entity.Sources.Select(SyncRuleMappingSourceDto.FromEntity).ToList()
        };
    }
}

/// <summary>
/// API representation of a SyncRuleMappingSource.
/// </summary>
public class SyncRuleMappingSourceDto
{
    public int Id { get; set; }
    public int Order { get; set; }
    public int? MetaverseAttributeId { get; set; }
    public string? MetaverseAttributeName { get; set; }
    public int? ConnectedSystemAttributeId { get; set; }
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// The expression to evaluate for this source.
    /// Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
    /// </summary>
    public string? Expression { get; set; }

    public static SyncRuleMappingSourceDto FromEntity(SyncRuleMappingSource entity)
    {
        return new SyncRuleMappingSourceDto
        {
            Id = entity.Id,
            Order = entity.Order,
            MetaverseAttributeId = entity.MetaverseAttributeId,
            MetaverseAttributeName = entity.MetaverseAttribute?.Name,
            ConnectedSystemAttributeId = entity.ConnectedSystemAttributeId,
            ConnectedSystemAttributeName = entity.ConnectedSystemAttribute?.Name,
            Expression = entity.Expression
        };
    }
}

/// <summary>
/// Request DTO for creating a new SyncRuleMapping.
/// </summary>
public class CreateSyncRuleMappingRequest
{
    /// <summary>
    /// For import rules: The target Metaverse Attribute ID.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// For export rules: The target Connected System Attribute ID.
    /// </summary>
    public int? TargetConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The sources for this mapping (attribute mappings or expressions).
    /// </summary>
    [Required]
    public List<CreateSyncRuleMappingSourceRequest> Sources { get; set; } = new();
}

/// <summary>
/// Request DTO for creating a SyncRuleMappingSource.
/// </summary>
public class CreateSyncRuleMappingSourceRequest
{
    /// <summary>
    /// The order of this source in the mapping.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// For export rules: The source Metaverse Attribute ID.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// For import rules: The source Connected System Attribute ID.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// An expression to evaluate for this source.
    /// Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
    /// Example: "CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"
    /// </summary>
    public string? Expression { get; set; }
}

/// <summary>
/// Request DTO for testing an expression with sample attribute data.
/// </summary>
public class TestExpressionRequest
{
    /// <summary>
    /// The expression to test.
    /// Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
    /// </summary>
    public string Expression { get; set; } = null!;

    /// <summary>
    /// Sample Metaverse attribute values to use during evaluation.
    /// Keys are attribute names, values are the attribute values.
    /// </summary>
    public Dictionary<string, object?>? MvAttributes { get; set; }

    /// <summary>
    /// Sample Connected System attribute values to use during evaluation.
    /// Keys are attribute names, values are the attribute values.
    /// </summary>
    public Dictionary<string, object?>? CsAttributes { get; set; }
}

/// <summary>
/// Response DTO for expression test results.
/// </summary>
public class TestExpressionResponse
{
    /// <summary>
    /// Indicates whether the expression is valid and evaluated successfully.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// The result of evaluating the expression (if successful).
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// The type of the result (e.g., "String", "Int32", "Boolean").
    /// </summary>
    public string? ResultType { get; set; }

    /// <summary>
    /// Error message if the expression is invalid or evaluation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Position in the expression where an error occurred (if applicable).
    /// </summary>
    public int? ErrorPosition { get; set; }
}
