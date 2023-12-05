using JIM.Models.Core;

namespace JIM.Models.Search
{
    public class PredefinedSearchCriteria
    {
        public int Id { get; set; }

        public PredefinedSearchComparisonType ComparisonType { get; set; }

        public string StringValue { get; set; } = null!;

        public MetaverseAttribute MetaverseAttribute { get; set; } = null!;
    }
}
