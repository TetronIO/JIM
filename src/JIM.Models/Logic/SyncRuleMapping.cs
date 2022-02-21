using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Logic
{
    public class SyncRuleMapping
    {
        public Guid Id { get; set; }
        public SyncRule SynchronisationRule { get; set; }
        /// <summary>
        /// When this is not the only sync rule mapping for the attribute, a priority helps us make decisions on system authority.
        /// A lower value is higher priority.
        /// </summary>
        public int Priority { get; set; }
        public List<SyncRuleMappingSource> Sources { get; set; }
        public MetaverseAttribute? TargetMetaverseAttribute { get; set; }
        public ConnectedSystemAttribute? TargetConnectedSystemAttribute { get; set; }

        public SyncRuleMapping()
        {
            Sources = new List<SyncRuleMappingSource>();
        }
    }
}
