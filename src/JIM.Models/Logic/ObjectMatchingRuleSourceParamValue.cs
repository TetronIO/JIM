using JIM.Models.Core;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Holds a parameter value for an ObjectMatchingRuleSource.
/// The value can come from an attribute or be a constant value.
/// Note: This class is retained for backwards compatibility but is not used with expression-based matching.
/// Expressions access attributes directly via mv["AttributeName"] and cs["AttributeName"] syntax.
/// </summary>
public class ObjectMatchingRuleSourceParamValue
{
    public int Id { get; set; }

    /// <summary>
    /// The name of this parameter.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Backlink to the parent ObjectMatchingRuleSource.
    /// </summary>
    public int ObjectMatchingRuleSourceId { get; set; }
    public ObjectMatchingRuleSource ObjectMatchingRuleSource { get; set; } = null!;

    /// <summary>
    /// For export matching: A Metaverse Attribute can be used as the source for this parameter.
    /// </summary>
    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// For import matching: A Connected System Attribute can be used as the source for this parameter.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }

    /// <summary>
    /// Holds a constant string value for this parameter.
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// Holds a constant DateTime value for this parameter.
    /// </summary>
    public DateTime DateTimeValue { get; set; }

    /// <summary>
    /// Holds a constant double value for this parameter.
    /// </summary>
    public double DoubleValue { get; set; }

    /// <summary>
    /// Holds a constant integer value for this parameter.
    /// </summary>
    public int IntValue { get; set; }

    /// <summary>
    /// Holds a constant boolean value for this parameter.
    /// </summary>
    public bool BoolValue { get; set; }
}
