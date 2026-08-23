// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request model for creating a Data Generation Template.
/// Referenced objects (Metaverse Object Types, Metaverse Attributes, Connected System attributes and
/// Example Data Sets) are identified by id only; resolve names to ids via their respective GET endpoints first.
/// </summary>
public class CreateExampleDataTemplateRequest
{
    /// <summary>
    /// The name of the template, e.g. "Demo Users". Must be unique across all Data Generation Templates.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The Object Types the template generates objects for, each with its per-attribute generation configuration.
    /// At least one Object Type is required.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one Object Type must be specified.")]
    public List<ExampleDataTemplateObjectTypeRequest> ObjectTypes { get; set; } = new();

    /// <summary>
    /// Optional reason for the creation, recorded against this template's change history.
    /// </summary>
    [StringLength(2000)]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Request model for updating a Data Generation Template.
/// Referenced objects are identified by id only; resolve names to ids via their respective GET endpoints first.
/// </summary>
public class UpdateExampleDataTemplateRequest
{
    /// <summary>
    /// The new name for the template. Omit (or pass null) to keep the existing name.
    /// Must be unique across all Data Generation Templates.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// The Object Types the template generates objects for. Omit (or pass null) to keep the template's
    /// existing Object Type graph unchanged; when supplied, the existing graph is replaced entirely by
    /// the Object Types in this list.
    /// </summary>
    public List<ExampleDataTemplateObjectTypeRequest>? ObjectTypes { get; set; }

    /// <summary>
    /// Optional reason for the update, recorded against this template's change history.
    /// </summary>
    [StringLength(2000)]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// One Object Type a Data Generation Template creates objects for.
/// </summary>
public class ExampleDataTemplateObjectTypeRequest
{
    /// <summary>
    /// The id of the Metaverse Object Type to generate objects for.
    /// </summary>
    [Required]
    public int MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// How many objects to generate for this Object Type when the template is executed.
    /// </summary>
    [Required]
    [Range(1, 1000000)]
    public int ObjectsToCreate { get; set; } = 1;

    /// <summary>
    /// The per-attribute generation configuration for this Object Type.
    /// </summary>
    public List<ExampleDataTemplateAttributeRequest> Attributes { get; set; } = new();
}

/// <summary>
/// How a Data Generation Template generates values for one attribute. Exactly one of
/// <see cref="MetaverseAttributeId"/> or <see cref="ConnectedSystemObjectTypeAttributeId"/> identifies
/// the attribute being generated; the remaining properties configure value generation and are validated
/// against the attribute's data type.
/// </summary>
public class ExampleDataTemplateAttributeRequest
{
    /// <summary>
    /// The id of the Metaverse Attribute to generate values for, when targeting the Metaverse.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The id of the Connected System attribute to generate values for, when targeting a Connected System.
    /// </summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; set; }

    /// <summary>
    /// What percentage (1-100) of generated objects receive a value for this attribute.
    /// Not compatible with <see cref="ManagerDepthPercentage"/>.
    /// </summary>
    public int? PopulatedValuesPercentage { get; set; }

    /// <summary>
    /// For boolean attributes, what percentage of generated values are true.
    /// </summary>
    public int? BoolTrueDistribution { get; set; }

    /// <summary>
    /// For boolean attributes, whether values are generated randomly.
    /// </summary>
    public bool? BoolShouldBeRandom { get; set; }

    /// <summary>
    /// For date attributes, the earliest value to generate (UTC).
    /// </summary>
    public DateTime? MinDate { get; set; }

    /// <summary>
    /// For date attributes, the latest value to generate (UTC).
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
    /// For number attributes, whether values are generated sequentially. Mutually exclusive with <see cref="RandomNumbers"/>.
    /// </summary>
    public bool? SequentialNumbers { get; set; }

    /// <summary>
    /// For number attributes, whether values are generated randomly. Mutually exclusive with <see cref="SequentialNumbers"/>.
    /// </summary>
    public bool? RandomNumbers { get; set; }

    /// <summary>
    /// For text attributes, a variable-replacement pattern that constructs values, e.g.
    /// "{Firstname}.{Lastname}[UniqueInt]@contoso.com", or index-based Example Data Set references such as "{0} {1}".
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>
    /// For text attributes, an optional expression that constructs values by reading and transforming other
    /// already-generated attributes on the same object via the mv["Attribute Name"] accessor. Mutually exclusive
    /// with <see cref="Pattern"/>, <see cref="ExampleDataSets"/> and <see cref="WeightedStringValues"/>.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// For text attributes, the Example Data Sets used to populate values, in order. Order positions can be
    /// referenced from <see cref="Pattern"/> via index-based variables, e.g. "{0} {1}".
    /// </summary>
    public List<ExampleDataTemplateDataSetInstanceRequest>? ExampleDataSets { get; set; }

    /// <summary>
    /// For text attributes, specific string values to choose from, weighted to control roughly how often each is selected.
    /// </summary>
    public List<ExampleDataTemplateWeightedValueRequest>? WeightedStringValues { get; set; }

    /// <summary>
    /// For Manager reference attributes, how far (1-99, as a percentage) into the organisational hierarchy
    /// managers should be present. Not compatible with <see cref="PopulatedValuesPercentage"/>.
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
    /// For reference attributes (other than Manager), the ids of the Metaverse Object Types to source
    /// referenced objects from.
    /// </summary>
    public List<int>? ReferenceMetaverseObjectTypeIds { get; set; }

    /// <summary>
    /// If generation of this attribute depends on another attribute's generated value, the dependency condition.
    /// </summary>
    public ExampleDataTemplateAttributeDependencyRequest? AttributeDependency { get; set; }
}

/// <summary>
/// One ordered Example Data Set reference used to populate a text attribute's values.
/// </summary>
public class ExampleDataTemplateDataSetInstanceRequest
{
    /// <summary>
    /// The id of the Example Data Set to draw values from.
    /// </summary>
    [Required]
    public int ExampleDataSetId { get; set; }

    /// <summary>
    /// The position of this Example Data Set relative to the attribute's other Example Data Sets, so it can be
    /// referenced reliably via numeric pattern variables, e.g. "{0} {1}".
    /// </summary>
    public int Order { get; set; }
}

/// <summary>
/// One weighted string value a text attribute can generate.
/// </summary>
public class ExampleDataTemplateWeightedValueRequest
{
    /// <summary>
    /// The string value to generate.
    /// </summary>
    [Required]
    public string Value { get; set; } = null!;

    /// <summary>
    /// The relative weight controlling roughly how often this value is selected compared to the attribute's other weighted values.
    /// </summary>
    public float Weight { get; set; }
}

/// <summary>
/// A condition that must hold on another attribute's generated value before this attribute is generated.
/// </summary>
public class ExampleDataTemplateAttributeDependencyRequest
{
    /// <summary>
    /// The id of the Metaverse Attribute whose generated value this attribute's generation depends on.
    /// </summary>
    [Required]
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The comparison to apply to the depended-on attribute's value: one of Equals, NotEquals, LessThan,
    /// GreaterThan, GreaterThanOrEqual, LessThanOrEqual or Like (case-insensitive).
    /// </summary>
    [Required]
    public string ComparisonType { get; set; } = null!;

    /// <summary>
    /// The value the depended-on attribute's generated value is compared against.
    /// </summary>
    [Required]
    public string StringValue { get; set; } = null!;
}
