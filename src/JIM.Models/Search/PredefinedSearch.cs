using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Search
{
    /// <summary>
    /// Enables users to find objects easily, and to control what attributes are returned in the search results
    /// </summary>
    [Index(nameof(Uri))]
    public class PredefinedSearch
    {
        public int Id { get; set; }
        public MetaverseObjectType MetaverseObjectType { get; set; }
        /// <summary>
        /// If true, this is the default predefined search for the associated metaverse object type. 
        /// This means in the web portal, a search parameter does not have to be used on the URL.
        /// </summary>
        public bool IsDefaultForMetaverseObjectType { get; set; }
        public string Name { get; set; }
        /// <summary>
        /// The uri component to use in URLs for the predefined search, i.e. "distribution" would result in: https://iga.tetron.io/t/groups/s/distribution
        /// </summary>
        public string Uri { get; set; }
        public bool BuiltIn { get; set; }
        public List<MetaverseAttribute> MetaverseAttributes { get; set; }
        public DateTime Created { get; set; }
        public List<PredefinedSearchCriteriaGroup> CriteriaGroups { get; set; }

        public PredefinedSearch()
        {
            MetaverseAttributes = new List<MetaverseAttribute>();
            CriteriaGroups = new List<PredefinedSearchCriteriaGroup>();
            Created = DateTime.Now;
        }
    }
}
