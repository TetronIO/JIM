using JIM.Models.Core;

namespace JIM.Models.Search
{
    public class PredefinedSearchAttribute
    {
        public int Id { get; set; }
        public PredefinedSearch PredefinedSearch { get; set; }
        public MetaverseAttribute MetaverseAttribute { get; set; }
        public int Position { get; set; }

        public PredefinedSearchAttribute()
        {
            Position = 0;
        }
    }
}
