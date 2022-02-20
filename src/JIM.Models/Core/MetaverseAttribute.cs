using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Core
{
    [Index(nameof(Name))]
    public class MetaverseAttribute : BaseAttribute
    {
        public MetaverseAttribute(string name) : base(name)
        {
        }
    }
}
