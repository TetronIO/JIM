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
        public int Id { get; set; }
        
        public int Order { get; set; }
        
        public MetaverseAttribute? MetaverseAttribute { get; set; }
        
        public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }
        
        public Function? Function { get; set; }
        
        public List<SyncRuleMappingSourceParamValue> ParameterValues { get; set; }

        public SyncRuleMappingSource()
        {
            ParameterValues = new List<SyncRuleMappingSourceParamValue>();
        }

        public bool IsValid()
        {
            // if we have no function, we require either a metaverse or connected system attribute value
            if (Function == null)
                return MetaverseAttribute != null || ConnectedSystemAttribute != null;

            // if we do have a function, we don't want either attribute values
            return MetaverseAttribute == null && ConnectedSystemAttribute == null;
        }
    }
}
