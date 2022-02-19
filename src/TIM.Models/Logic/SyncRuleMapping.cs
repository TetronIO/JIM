using TIM.Models.Core;

namespace TIM.Models.Logic
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
        public BaseAttribute Target { get; set; }

        public SyncRuleMapping(SyncRule synchronisationRule, BaseAttribute target)
        {
            SynchronisationRule = synchronisationRule;
            Target = target;
            Sources = new List<SyncRuleMappingSource>();
        }
    }
}
