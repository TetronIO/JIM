using JIM.Models.Core;
using JIM.Models.Extensibility;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines a source for an object matching rule. Can hold either an attribute or a function.
/// If it's an attribute, it will be either a ConnectedSystemAttribute (for CSO → MVO matching)
/// or a MetaverseAttribute (for MVO → CSO matching during export).
/// </summary>
public class ObjectMatchingRuleSource
{
    public int Id { get; set; }

    /// <summary>
    /// If multiple sources are defined (for function chaining), this determines evaluation order.
    /// Sources are evaluated in ascending order (0, 1, 2, etc.).
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Backlink to the parent ObjectMatchingRule.
    /// </summary>
    public int ObjectMatchingRuleId { get; set; }
    public ObjectMatchingRule ObjectMatchingRule { get; set; } = null!;

    /// <summary>
    /// For import matching: The Connected System attribute to use as the source value.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }

    /// <summary>
    /// For export matching: The Metaverse attribute to use as the source value.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }
    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// If populated, a function (built-in or extensible) should be used to determine the match value.
    /// </summary>
    public Function? Function { get; set; }

    /// <summary>
    /// If a Function is used, parameter values for the function call.
    /// </summary>
    public List<ObjectMatchingRuleSourceParamValue> ParameterValues { get; set; } = new();

    /// <summary>
    /// Validates that the source is correctly configured.
    /// Must have either an attribute (CS or MV) or a function, but not both.
    /// </summary>
    public bool IsValid()
    {
        // If we have no Function, we require either a metaverse or connected system attribute
        if (Function == null)
            return MetaverseAttribute != null || ConnectedSystemAttribute != null;

        // If we do have a Function, we don't want either attribute values
        return MetaverseAttribute == null && ConnectedSystemAttribute == null;
    }
}
