using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Logic
{
    /// <summary>
    /// Defines how data should flow from JIM to a connected system (or visa versa) for a specific attribute.
    /// Not, if transforms are being applied, then multiple attributes could be the source, but only a single attribute can be a target. i.e. n:1
    /// </summary>
    public class SyncRuleMapping
    {
        public int Id { get; set; }

        public SyncRule SynchronisationRule { get; set; } = null!;

        /// <summary>
        /// When this is not the only sync rule mapping for the attribute, a priority helps us make decisions on system authority.
        /// A lower value denotes a higher priority. 0 is the highest priority.
        /// </summary>
        public int Priority { get; set; }

        public List<SyncRuleMappingSource> Sources { get; set; }

        public MetaverseAttribute? TargetMetaverseAttribute { get; set; }

        public ConnectedSystemObjectTypeAttribute? TargetConnectedSystemAttribute { get; set; }

        public SyncRuleMapping()
        {
            Sources = new List<SyncRuleMappingSource>();
        }
    }
}
