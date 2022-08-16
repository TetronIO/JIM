namespace JIM.Models.Search
{
    public class PredefinedSearchCriteriaGroup
    {
        public int Id { get; set; }
        public PredefinedSearchGroupType Type { get; set; }
        public List<PredefinedSearchCriteria> Criteria { get; set; }
        public int Position { get; set; }

        public PredefinedSearchCriteriaGroup()
        {
            Criteria = new List<PredefinedSearchCriteria>();
            Position = 0;
        }
    }
}
