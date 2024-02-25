using JIM.Models.Core;
using JIM.Models.Extensibility;
using JIM.Models.Staging;

namespace JIM.Models.Logic
{
    /// <summary>
    /// A SyncRuleMappingSourceParamValue can get it's value from an attribute, or a constant value from type-specific properties.
    /// If it is an attribute, then depending on the direction of the sync rule (import/export), then it'll be either the 
    /// ConnectedSystemAttribute or MetaverseAttribute that needs to be populated.
    /// </summary>
    public class SyncRuleMappingSourceParamValue
    {
        public int Id { get; set; }
        
        public FunctionParameter FunctionParameter { get; set; } = null!;

        public MetaverseAttribute? MetaverseAttribute { get; set; }

        public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }

        public string? StringValue { get; set; }

        public DateTime DateTimeValue { get; set; }

        public double DoubleValue { get; set; }
    }
}
