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
        public string Name { get; set; }
        /// <summary>
        /// The uri component to use in URLs for the predefined search, i.e. "distribution" would result in: https://iga.tetron.io/t/groups/s/distribution
        /// </summary>
        public string Uri { get; set; }
        public bool BuiltIn { get; set; }
        public List<MetaverseAttribute> MetaverseAttributes { get; set; }
        public DateTime Created { get; set; }

        //todo: criteria for custom searches

        public PredefinedSearch()
        {
            MetaverseAttributes = new List<MetaverseAttribute>();
            Created = DateTime.Now;
        }
    }
}
