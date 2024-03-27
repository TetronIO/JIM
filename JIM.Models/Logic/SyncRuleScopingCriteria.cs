using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Models.Logic
{
    public class SyncRuleScopingCriteria
    {
        public int Id { get; set; }

        public SearchComparisonType ComparisonType { get; set; }

        public string StringValue { get; set; } = null!;

        public MetaverseAttribute MetaverseAttribute { get; set; } = null!;
    }
}
