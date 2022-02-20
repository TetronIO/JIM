using JIM.Models.Core;
using JIM.Models.Extensibility;

namespace JIM.Models.Logic
{
    /// <summary>
    /// A SyncRuleMappingSourceParamValue can get it's value from an attribute, or a constant value from type-specific properties.
    /// </summary>
    public class SyncRuleMappingSourceParamValue
    {
        public Guid Id { get; set; }
        public FunctionParameter FunctionParameter { get; set; }
        public BaseAttribute? Attribute { get; set; }
        public string? StringValue { get; set; }
        public DateTime DateTimeValue { get; set; }
        public double DoubleValue { get; set; }

        public SyncRuleMappingSourceParamValue()
        {
        }
    }
}
