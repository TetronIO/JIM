using JIM.Models.Core;
using JIM.Models.Extensibility;
using JIM.Models.Staging;

namespace JIM.Models.Logic
{
    /// <summary>
    /// Can hold either an attribute, or a function but not none.
    /// If it is an attribute, then depending on the direction of the sync rule (import/export), then it'll
    /// be either the ConnectedSystemAttribute or MetaverseAttribute that needs to be populated.
    /// </summary>
    public class SyncRuleMappingSource
    {
        public Guid Id { get; set; }
        public int Order { get; set; }

        public MetaverseAttribute? MetaverseAttribute { get; set; }
        public ConnectedSystemAttribute? ConnectedSystemAttribute { get; set; }

        public Function? Function { get; set; }
        public List<SyncRuleMappingSourceParamValue> ParameterValues { get; set; }

        public SyncRuleMappingSource()
        {
            ParameterValues = new List<SyncRuleMappingSourceParamValue>();
        }
    }
}
