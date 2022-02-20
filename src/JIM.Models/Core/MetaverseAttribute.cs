using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Core
{
    [Index(nameof(Name))]
    public class MetaverseAttribute : BaseAttribute
    {
        public MetaverseAttribute()
        {
        }
    }
}
