using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
using System;

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
            var predefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync(uri);
            if (predefinedSearch != null)
                predefinedSearch = PostProcessPredefinedSearch(predefinedSearch);

            return predefinedSearch;
        }

        /// <summary>
        /// Attempts to retrieve a default predefined search for a given metaverse object type
        /// </summary>
        public async Task<PredefinedSearch?> GetPredefinedSearchAsync(MetaverseObjectType metaverseObjectType)
        {
            var predefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync(metaverseObjectType);
            if (predefinedSearch != null)
                predefinedSearch = PostProcessPredefinedSearch(predefinedSearch);

            return predefinedSearch;
        }

        private static PredefinedSearch PostProcessPredefinedSearch(PredefinedSearch predefinedSearch)
        {
            predefinedSearch.Attributes = predefinedSearch.Attributes.OrderBy(q => q.Position).ToList();
            return predefinedSearch;
        }
        #endregion
    }
}
