using JIM.Models.Search;
using JIM.Models.Search.Dto;

namespace JIM.Data.Repositories
{
    public interface ISearchRepository
    {
        public Task<IList<PredefinedSearchHeader>> GetPredefinedSearchHeadersAsync();
        public Task<PredefinedSearch?> GetPredefinedSearchAsync(string uri);
    }
}
