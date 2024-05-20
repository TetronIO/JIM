using JIM.Models.Core;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines how data should flow from JIM to a connected system (or vice versa) for a specific attribute.
/// Mappings can be used for either attribute flow, or object matching scenarios. They have slightly different rules:
/// 
/// Rules:
/// 
/// ** Object Matching Scenario **
/// 
/// There can be multiple mappings against a sync rule for the same target Metaverse attribute.
/// i.e. you might have a primary connected system attribute you want to join on defined in one mapping, and then match on a fall-back attribute in another mapping.
/// 
/// ** Attribute Flow Scenario **
/// 
/// There can only be one mapping per target attribute.
/// i.e. you might be mapping a single attribute source attribute to a target attribute, or your might use a function or expression as a 
/// source and in that be using multiple attributes as sources (parameters).
/// </summary>
public class SyncRuleMapping
{
    public int Id { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public MetaverseObject? CreatedBy { get; set; }

    /// <summary>
    /// Applies to: Object Matching scenario.
    /// If multiple mappings are defined for a target attribute, then the order in which they appear matters.
    /// Mappings will be evaluated in order, i.e. order 0 item will be evaluated first, then 1, etc.
    /// Does not apply to attribute flow rules. This wouldn't make sense. There can only be one mapping for each target attribute.
    /// </summary>
    public int? Order { get; set; }

    /// <summary>
    /// A backlink to the parent SynchronisationRule for when this is an AttributeFlow type SyncRuleMapping.
    /// </summary>
    public SyncRule? AttributeFlowSynchronisationRule { get; set; }

    /// <summary>
    /// A backlink to the parent SynchronisationRule for when this is an ObjectMatching type SyncRuleMapping.
    /// </summary>
    public SyncRule? ObjectMatchingSynchronisationRule { get; set; }

    /// <summary>
    /// Denotes what the purpose of this mapping is for, i.e. attribute flow, or object matching (aka joining/correlating).
    /// </summary>
    public SyncRuleMappingType Type { get; set; } = SyncRuleMappingType.NotSet;

    /// <summary>
    /// The sources that provide the value for the target attribute when the mapping is evaluated. 
    /// Supported scenarios:
    /// - Just one: for mapping a single attribute to the target attribute. i.e. attribute_1 => attribute
    /// - Just one: for using a single function to generate a value for the target attribute, i.e. Trim(attribute) => attribute
    /// - Just one: for using an expression to generate a value for the target attribute. i.e. attribute_1 ?? attribute_2 => attribute
    /// - Multiple: for using multiple function calls that chain through each other to generate a value for the target attribute.
    /// </summary>
    public List<SyncRuleMappingSource> Sources { get; } = new();

    /// <summary>
    /// For an import rule, this is where the imported attribute value ends up being assigned to.
    /// Also, where Object Matching Rules map to. i.e. a Connected System Attribute (source) might be a direct, or transform comparison to the Metaverse Object (target) attribute.
    /// </summary>
    public MetaverseAttribute? TargetMetaverseAttribute { get; set; }

    /// <summary>
    /// For an export rule, this is where the exported attribute value ends up being assigned to.
    /// Does not apply to Object Matching Rules, as the target is always a Metaverse attribute in that context.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? TargetConnectedSystemAttribute { get; set; }

    /// <summary>
    /// Helper method to provide a description for the user on what type of source configuration this is.
    /// </summary>
    public SyncRuleMappingSourcesType GetSourceType()
    {
        if (Sources.Count == 0)
            return SyncRuleMappingSourcesType.NotSet;

        if (Sources.All(s => s.ConnectedSystemAttribute != null || s.MetaverseAttribute != null))
            return SyncRuleMappingSourcesType.AttributeMapping;

        if (Sources.All(s => s.Function != null))
            return SyncRuleMappingSourcesType.FunctionMapping;

        // expressions not yet supported
        return SyncRuleMappingSourcesType.AdvancedMapping;
    }
}
