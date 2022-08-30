namespace JIM.Models.Search
{
    public class PredefinedSearchCriteriaGroup
    {
        public int Id { get; set; }
        public PredefinedSearchGroupType Type { get; set; }
        public List<PredefinedSearchCriteria> Criteria { get; set; }
        public int Position { get; set; }
        /// <summary>
        /// PredefinedSearchCriteriaGroups can be nested, to enable more complex queries to be constructued, i.e. ANY(ALL(x=1,y=2),ANY(c=1,d=1))
        /// </summary>
        public List<PredefinedSearchCriteriaGroup> ChildGroups { get; set; }
        /// <summary>
        /// Navigation property for child groups
        /// </summary>
        public PredefinedSearchCriteriaGroup? ParentGroup { get; set; }

        public PredefinedSearchCriteriaGroup()
        {
            Criteria = new List<PredefinedSearchCriteria>();
            ChildGroups = new List<PredefinedSearchCriteriaGroup>();
            Position = 0;
        }
    }
}
