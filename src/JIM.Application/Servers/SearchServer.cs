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
        #endregion
    }
}
