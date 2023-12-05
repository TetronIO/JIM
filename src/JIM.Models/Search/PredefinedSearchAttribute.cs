using JIM.Models.Core;

namespace JIM.Models.Search
{
    public class PredefinedSearchAttribute
    {
        public int Id { get; set; }

        public PredefinedSearch PredefinedSearch { get; set; } = null!;

        public MetaverseAttribute MetaverseAttribute { get; set; } = null!;

        public int Position { get; set; } = 0;
    }
}
