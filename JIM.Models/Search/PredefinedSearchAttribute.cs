using JIM.Models.Core;

namespace JIM.Models.Search
{
    public class PredefinedSearchAttribute
    {
        public int Id { get; set; }

        public PredefinedSearch PredefinedSearch { get; set; } = null!;

        public MetaverseAttribute MetaverseAttribute { get; set; } = null!;

        /// <summary>
        /// Predefined search attributes are shown to the user in a left-right order as determined by this value. 0 is the first attribute to be shown.
        /// </summary>
        public int Position { get; set; } = 0;
    }
}
