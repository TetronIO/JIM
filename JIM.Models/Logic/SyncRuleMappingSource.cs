using JIM.Models.Core;
using JIM.Models.Extensibility;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Can hold either an attribute, or a function but not none.
/// If it is an attribute, then depending on the direction of the sync rule (import/export), then it'll
/// be either the ConnectedSystemAttribute or MetaverseAttribute that needs to be populated.
/// </summary>
public class SyncRuleMappingSource
{
    public int Id { get; set; }

    /// <summary>
    /// Applies to: Function chaining scenario.
    /// If multiple sources are defined against a mapping, then the order in which they appear matters.
    /// Sources will be evaluated in order, i.e order 0 item will be evaluated first, then 1, etc.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// For Export sync rules only: If populated, denotes that a Metaverse Attribute should be used to set the target attribute value.
    /// </summary>
    public MetaverseAttribute? MetaverseAttribute { get; set; }
        
    /// <summary>
    /// For Import sync rules only: If populated, denotes that a Connected System Attribute should be used to set the target attribute value.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }
        
    /// <summary>
    /// If populated, denotes that a Function (either built-in or extensible) should be used to determine the target attribute value.
    /// </summary>
    public Function? Function { get; set; }

    // todo: add in support for Expressions as an alternative to Functions and Attributes.
        
    /// <summary>
    /// If a Function or Expression is to be the source of the target attribute value, then parameters for those need to be defined.
    /// They in term can be sourced from attributes, or constant values.
    /// </summary>
    public List<SyncRuleMappingSourceParamValue> ParameterValues { get; set; } = new();

    public bool IsValid()
    {
        // if we have no Function, we require either a metaverse or connected system attribute value to use as the source
        if (Function == null)
            return MetaverseAttribute != null || ConnectedSystemAttribute != null;

        // if we do have a Function, we don't want either attribute values
        return MetaverseAttribute == null && ConnectedSystemAttribute == null;
    }
}