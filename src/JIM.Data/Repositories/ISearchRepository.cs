using JIM.Models.Search.Dto;

namespace JIM.Data.Repositories
{
    public interface ISearchRepository
    {
        public Task<IList<PredefinedSearchHeader>> GetPredefinedSearchHeadersAsync();
    }
}
