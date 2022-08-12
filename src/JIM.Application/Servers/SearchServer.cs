using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.Dto;

namespace JIM.Application.Search
{
    public class SearchServer
    {
        #region accessors
        private JimApplication Application { get; }
        #endregion

        #region constructors
        internal SearchServer(JimApplication application)
        {
            Application = application;
        }
        #endregion

        #region predefined searches
        public async Task<IList<PredefinedSearchHeader>> GetPredefinedSearchHeadersAsync()
        {
            return await Application.Repository.Search.GetPredefinedSearchHeadersAsync();
        }

        public async Task<PredefinedSearch?> GetPredefinedSearchAsync(string uri)
        {
            return await Application.Repository.Search.GetPredefinedSearchAsync(uri);
        }

        /// <summary>
        /// Attempts to retrieve a default predefined search for a given metaverse object type
        /// </summary>
        public async Task<PredefinedSearch?> GetPredefinedSearchAsync(MetaverseObjectType metaverseObjectType)
        {
            return await Application.Repository.Search.GetPredefinedSearchAsync(metaverseObjectType);
        }
        #endregion
    }
}
