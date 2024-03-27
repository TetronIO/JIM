using JIM.Models.Search;

namespace JIM.Models.Logic
{
    public class SyncRuleScopingCriteriaGroup
    {
        public int Id { get; set; }

        public SearchGroupType Type { get; set; }

        public List<SyncRuleScopingCriteria> Criteria { get; set; }

        public int Position { get; set; }

        /// <summary>
        /// SyncRuleScopingGroup can be nested, to enable more complex queries to be constructued, i.e. ANY(ALL(x=1,y=2),ANY(c=1,d=1))
        /// </summary>
        public List<SyncRuleScopingCriteriaGroup> ChildGroups { get; set; }

        /// <summary>
        /// Navigation property for child groups
        /// </summary>
        public SyncRuleScopingCriteriaGroup? ParentGroup { get; set; }

        public SyncRuleScopingCriteriaGroup()
        {
            Criteria = new List<SyncRuleScopingCriteria>();
            ChildGroups = new List<SyncRuleScopingCriteriaGroup>();
            Position = 0;
        }
    }
}
