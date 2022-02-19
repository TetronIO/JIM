using TIM.Models.Core;
using TIM.Models.Extensibility;

namespace TIM.Models.Logic
{
    /// <summary>
    /// Can hold either an attribute, or a function but not none.
    /// </summary>
    public class SyncRuleMappingSource
    {
        public Guid Id { get; set; }
        public int Order { get; set; }
        public BaseAttribute? Attribute { get; set; }
        public Function? Function { get; set; }
        public List<SyncRuleMappingSourceParamValue> ParameterValues { get; set; }

        public SyncRuleMappingSource()
        {
            ParameterValues = new List<SyncRuleMappingSourceParamValue>();
        }
    }
}
