using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Logic
{
    /// <summary>
    /// Defines how data should flow from JIM to a connected system (or visa versa) for a specific attribute.
    /// Note: There can be only one mapping per target attribute. If multiple sources need to be used to determine the target value
    /// then the user is to define multiple sources under a mapping. This keeps the UI clean: there's only one mapping per target attribute.
    /// How those target attribute values are determined is up to the mapping.
    /// </summary>
    public class SyncRuleMapping
    {
        public int Id { get; set; }

        public DateTime Created { get; set; }

        public MetaverseObject? CreatedBy { get; set; }

        /// <summary>
        /// A link to the parent SynchronisationRule for when this is an AttributeFlow type SyncRuleMapping.
        /// </summary>
        public SyncRule? AttributeFlowSynchronisationRule { get; set; }

        /// <summary>
        /// A link to the parent SynchronisationRule for when this is an ObjectMatching type SyncRuleMapping.
        /// </summary>
        public SyncRule? ObjectMatchingSynchronisationRule { get; set; }

        /// <summary>
        /// Denotes what the purpose of this mapping is for, i.e. attribute flow, or object matching (joining/correlating).
        /// </summary>
        public SyncRuleMappingType Type { get; set; }

        /// <summary>
        /// The list of sources to use when determining the target value.
        /// A single source is most common, i.e. a 1:1 mapping, but it's possible to use multiple sources to determine the target value,
        /// i.e. you might want to take the first non-null value from a list of sources, or you might want to use a function or expression to
        /// make more complex decisions on what the target value should be.
        /// </summary>
        public List<SyncRuleMappingSource> Sources { get; set; }

        public MetaverseAttribute? TargetMetaverseAttribute { get; set; }

        public ConnectedSystemObjectTypeAttribute? TargetConnectedSystemAttribute { get; set; }

        public SyncRuleMapping()
        {
            Type = SyncRuleMappingType.NotSet;
            Sources = new List<SyncRuleMappingSource>();
            Created = DateTime.UtcNow;
        }
    }
}
