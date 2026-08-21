// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.ExampleData;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of an Example Data Set, including its values. Replaces the raw entity response (#1447).
/// </summary>
public class ExampleDataSetDto
{
    /// <summary>
    /// The unique identifier of the Example Data Set.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the Example Data Set, e.g. "Firstnames (Female)".
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The .NET culture code the values are in, e.g. "en-GB".
    /// </summary>
    public string Culture { get; set; } = null!;

    /// <summary>
    /// Whether the Example Data Set ships with JIM (as opposed to being administrator-created).
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// When the Example Data Set was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The display name of the principal that created the Example Data Set.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the Example Data Set was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The display name of the principal that last modified the Example Data Set.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The values in the Example Data Set.
    /// </summary>
    public List<ExampleDataSetValueDto> Values { get; set; } = new();

    /// <summary>
    /// Creates a DTO from an entity. The entity's Values collection should be populated.
    /// </summary>
    public static ExampleDataSetDto FromEntity(ExampleDataSet entity)
    {
        return new ExampleDataSetDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Culture = entity.Culture,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            CreatedByName = entity.CreatedByName,
            LastUpdated = entity.LastUpdated,
            LastUpdatedByName = entity.LastUpdatedByName,
            Values = entity.Values.Select(v => new ExampleDataSetValueDto { Id = v.Id, StringValue = v.StringValue }).ToList()
        };
    }
}

/// <summary>
/// API representation of a single Example Data Set value.
/// </summary>
public class ExampleDataSetValueDto
{
    /// <summary>
    /// The unique identifier of the value.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The value itself, e.g. "London".
    /// </summary>
    public string StringValue { get; set; } = null!;
}

/// <summary>
/// API representation of a Data Generation Template, including its per-Object-Type attribute configuration.
/// Replaces the raw entity response (#1447), whose graph reached live schema entities
/// (ExampleDataTemplateAttribute carries a ConnectedSystemObjectTypeAttribute) and was one navigation away
/// from killing OpenAPI document generation; the DTO carries referenced objects as ids and names.
/// </summary>
public class ExampleDataTemplateDto
{
    /// <summary>
    /// The unique identifier of the template.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the template, e.g. "Demo Users".
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Whether the template ships with JIM (as opposed to being administrator-created).
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// When the template was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The display name of the principal that created the template.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the template was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The display name of the principal that last modified the template.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The Object Types the template generates objects for.
    /// </summary>
    public List<ExampleDataTemplateObjectTypeDto> ObjectTypes { get; set; } = new();

    /// <summary>
    /// Creates a DTO from an entity. The entity's ObjectTypes graph should be populated.
    /// </summary>
    public static ExampleDataTemplateDto FromEntity(ExampleDataTemplate entity)
    {
        return new ExampleDataTemplateDto
        {
            Id = entity.Id,
            Name = entity.Name,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            CreatedByName = entity.CreatedByName,
            LastUpdated = entity.LastUpdated,
            LastUpdatedByName = entity.LastUpdatedByName,
            ObjectTypes = entity.ObjectTypes.Select(ExampleDataTemplateObjectTypeDto.FromEntity).ToList()
        };
    }
}

/// <summary>
/// API representation of one Object Type a Data Generation Template creates objects for.
/// </summary>
public class ExampleDataTemplateObjectTypeDto
{
    /// <summary>
    /// The unique identifier of the template Object Type configuration.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The id of the Metaverse Object Type objects are generated for.
    /// </summary>
    public int MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Object Type objects are generated for.
    /// </summary>
    public string MetaverseObjectTypeName { get; set; } = null!;

    /// <summary>
    /// How many objects the template generates for this Object Type.
    /// </summary>
    public int ObjectsToCreate { get; set; }

    /// <summary>
    /// The per-attribute generation configuration.
    /// </summary>
    public List<ExampleDataTemplateAttributeDto> TemplateAttributes { get; set; } = new();

    /// <summary>
    /// Creates a DTO from an entity. The entity's MetaverseObjectType and TemplateAttributes should be populated.
    /// </summary>
    public static ExampleDataTemplateObjectTypeDto FromEntity(ExampleDataObjectType entity)
    {
        return new ExampleDataTemplateObjectTypeDto
        {
            Id = entity.Id,
            MetaverseObjectTypeId = entity.MetaverseObjectType.Id,
            MetaverseObjectTypeName = entity.MetaverseObjectType.Name,
            ObjectsToCreate = entity.ObjectsToCreate,
            TemplateAttributes = entity.TemplateAttributes.Select(ExampleDataTemplateAttributeDto.FromEntity).ToList()
        };
    }
}

/// <summary>
/// API representation of how a Data Generation Template generates values for one attribute.
/// The attribute being generated is identified by id and name: MetaverseAttribute for Metaverse-targeted
/// generation, ConnectedSystemObjectTypeAttribute for Connected-System-targeted generation.
/// </summary>
public class ExampleDataTemplateAttributeDto
{
    /// <summary>
    /// The unique identifier of the template attribute configuration.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The id of the Metaverse Attribute values are generated for, when targeting the Metaverse.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Attribute values are generated for, when targeting the Metaverse.
    /// </summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// The id of the Connected System attribute values are generated for, when targeting a Connected System.
    /// </summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; set; }

    /// <summary>
    /// The name of the Connected System attribute values are generated for, when targeting a Connected System.
    /// </summary>
    public string? ConnectedSystemObjectTypeAttributeName { get; set; }

    /// <summary>
    /// What percentage of generated objects receive a value for this attribute.
    /// </summary>
    public int? PopulatedValuesPercentage { get; set; }

    /// <summary>
    /// For boolean attributes, what percentage of values are true.
    /// </summary>
    public int? BoolTrueDistribution { get; set; }

    /// <summary>
    /// For boolean attributes, whether values are generated randomly.
    /// </summary>
    public bool? BoolShouldBeRandom { get; set; }

    /// <summary>
    /// For date attributes, the earliest value to generate.
    /// </summary>
    public DateTime? MinDate { get; set; }

    /// <summary>
    /// For date attributes, the latest value to generate.
    /// </summary>
    public DateTime? MaxDate { get; set; }

    /// <summary>
    /// For number attributes, the smallest value to generate.
    /// </summary>
    public int? MinNumber { get; set; }

    /// <summary>
    /// For number attributes, the largest value to generate.
    /// </summary>
    public int? MaxNumber { get; set; }

    /// <summary>
    /// For number attributes, whether values are generated sequentially.
    /// </summary>
    public bool? SequentialNumbers { get; set; }

    /// <summary>
    /// For number attributes, whether values are generated randomly.
    /// </summary>
    public bool? RandomNumbers { get; set; }

    /// <summary>
    /// A variable-replacement pattern for constructing string values, e.g. "{Firstname}.{Lastname}@contoso.com".
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>
    /// An expression that constructs the value from other already-generated attributes via mv["Attribute Name"].
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// The Example Data Sets values are drawn from, in order.
    /// </summary>
    public List<ExampleDataTemplateDataSetInstanceDto> ExampleDataSetInstances { get; set; } = new();

    /// <summary>
    /// Specific string values to choose from, weighted to control their distribution.
    /// </summary>
    public List<ExampleDataTemplateWeightedValueDto>? WeightedStringValues { get; set; }

    /// <summary>
    /// For Manager attributes, how far into the organisational hierarchy managers are present.
    /// </summary>
    public int? ManagerDepthPercentage { get; set; }

    /// <summary>
    /// For multi-valued reference attributes, the minimum number of values to assign.
    /// </summary>
    public int? MvaRefMinAssignments { get; set; }

    /// <summary>
    /// For multi-valued reference attributes, the maximum number of values to assign.
    /// </summary>
    public int? MvaRefMaxAssignments { get; set; }

    /// <summary>
    /// For reference attributes, the Metaverse Object Types generated references may point at.
    /// </summary>
    public List<ExampleDataTemplateReferenceTypeDto>? ReferenceMetaverseObjectTypes { get; set; }

    /// <summary>
    /// The condition on another attribute that must hold for this attribute to be generated, if any.
    /// </summary>
    public ExampleDataTemplateAttributeDependencyDto? AttributeDependency { get; set; }

    /// <summary>
    /// Creates a DTO from an entity. The entity's navigation properties should be populated.
    /// </summary>
    public static ExampleDataTemplateAttributeDto FromEntity(ExampleDataTemplateAttribute entity)
    {
        return new ExampleDataTemplateAttributeDto
        {
            Id = entity.Id,
            MetaverseAttributeId = entity.MetaverseAttribute?.Id,
            MetaverseAttributeName = entity.MetaverseAttribute?.Name,
            ConnectedSystemObjectTypeAttributeId = entity.ConnectedSystemObjectTypeAttribute?.Id,
            ConnectedSystemObjectTypeAttributeName = entity.ConnectedSystemObjectTypeAttribute?.Name,
            PopulatedValuesPercentage = entity.PopulatedValuesPercentage,
            BoolTrueDistribution = entity.BoolTrueDistribution,
            BoolShouldBeRandom = entity.BoolShouldBeRandom,
            MinDate = entity.MinDate,
            MaxDate = entity.MaxDate,
            MinNumber = entity.MinNumber,
            MaxNumber = entity.MaxNumber,
            SequentialNumbers = entity.SequentialNumbers,
            RandomNumbers = entity.RandomNumbers,
            Pattern = entity.Pattern,
            Expression = entity.Expression,
            ExampleDataSetInstances = entity.ExampleDataSetInstances
                .Select(i => new ExampleDataTemplateDataSetInstanceDto
                {
                    Id = i.Id,
                    ExampleDataSetId = i.ExampleDataSet.Id,
                    ExampleDataSetName = i.ExampleDataSet.Name,
                    Order = i.Order
                })
                .ToList(),
            WeightedStringValues = entity.WeightedStringValues?
                .Select(w => new ExampleDataTemplateWeightedValueDto { Id = w.Id, Value = w.Value, Weight = w.Weight })
                .ToList(),
            ManagerDepthPercentage = entity.ManagerDepthPercentage,
            MvaRefMinAssignments = entity.MvaRefMinAssignments,
            MvaRefMaxAssignments = entity.MvaRefMaxAssignments,
            ReferenceMetaverseObjectTypes = entity.ReferenceMetaverseObjectTypes?
                .Select(t => new ExampleDataTemplateReferenceTypeDto { Id = t.Id, Name = t.Name })
                .ToList(),
            AttributeDependency = entity.AttributeDependency == null
                ? null
                : new ExampleDataTemplateAttributeDependencyDto
                {
                    Id = entity.AttributeDependency.Id,
                    MetaverseAttributeId = entity.AttributeDependency.MetaverseAttribute.Id,
                    MetaverseAttributeName = entity.AttributeDependency.MetaverseAttribute.Name,
                    ComparisonType = entity.AttributeDependency.ComparisonType.ToString(),
                    StringValue = entity.AttributeDependency.StringValue
                }
        };
    }
}

/// <summary>
/// API representation of one Example Data Set a template attribute draws values from.
/// </summary>
public class ExampleDataTemplateDataSetInstanceDto
{
    /// <summary>
    /// The unique identifier of the data set instance.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The id of the Example Data Set values are drawn from.
    /// </summary>
    public int ExampleDataSetId { get; set; }

    /// <summary>
    /// The name of the Example Data Set values are drawn from.
    /// </summary>
    public string ExampleDataSetName { get; set; } = null!;

    /// <summary>
    /// The position of this data set among the attribute's data sets (used by index-based patterns like "{0} {1}").
    /// </summary>
    public int Order { get; set; }
}

/// <summary>
/// API representation of one weighted string value a template attribute chooses from.
/// </summary>
public class ExampleDataTemplateWeightedValueDto
{
    /// <summary>
    /// The unique identifier of the weighted value.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The value to generate, e.g. "active".
    /// </summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// The relative weight controlling roughly how often the value is chosen.
    /// </summary>
    public float Weight { get; set; }
}

/// <summary>
/// API representation of a Metaverse Object Type a generated reference attribute may point at.
/// </summary>
public class ExampleDataTemplateReferenceTypeDto
{
    /// <summary>
    /// The id of the Metaverse Object Type.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the Metaverse Object Type.
    /// </summary>
    public string Name { get; set; } = null!;
}

/// <summary>
/// API representation of a template attribute's generation condition on another attribute.
/// </summary>
public class ExampleDataTemplateAttributeDependencyDto
{
    /// <summary>
    /// The unique identifier of the dependency.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The id of the Metaverse Attribute the condition evaluates.
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Attribute the condition evaluates.
    /// </summary>
    public string MetaverseAttributeName { get; set; } = null!;

    /// <summary>
    /// The comparison operator, e.g. "Equals".
    /// </summary>
    public string ComparisonType { get; set; } = null!;

    /// <summary>
    /// The value the evaluated attribute must hold.
    /// </summary>
    public string StringValue { get; set; } = null!;
}
