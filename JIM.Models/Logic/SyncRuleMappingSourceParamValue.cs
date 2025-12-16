using JIM.Models.Core;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// A SyncRuleMappingSourceParamValue supports the calling of a function as part of a sync rule mapping.
/// A SyncRuleMappingSourceParamValue can get its value from an attribute, or a constant value from type-specific properties.
/// If it is an attribute, then depending on the direction of the sync rule (import/export), then it'll be either the
/// ConnectedSystemAttribute or MetaverseAttribute that needs to be populated.
/// Note: This class is retained for backwards compatibility but is not used with expression-based mappings.
/// Expressions access attributes directly via mv["AttributeName"] and cs["AttributeName"] syntax.
/// </summary>
public class SyncRuleMappingSourceParamValue
{
    public int Id { get; set; }

    /// <summary>
    /// The name to give the parameter.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// For export only: A Metaverse Attribute can be used as the source for this parameter.
    /// </summary>
    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// For import only: A Connected System Attribute can be used as the source for this parameter.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }

    /// <summary>
    /// Holds a constant string to use as the value of the param.
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// Holds a constant datetime to use as the value of the param.
    /// </summary>
    public DateTime DateTimeValue { get; set; }

    /// <summary>
    /// Holds a constant double number to use as the value of the param.
    /// </summary>
    public double DoubleValue { get; set; }

    /// <summary>
    /// Holds a constant integer number to use as the value of the param.
    /// </summary>
    public int IntValue { get; set; }

    /// <summary>
    /// Holds a constant boolean to use as the value of the param.
    /// </summary>
    public bool BoolValue { get; set; }
}