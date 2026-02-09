using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
using Microsoft.EntityFrameworkCore;
namespace JIM.PostgresData.Repositories;

public class SearchRepository : ISearchRepository
{
    private PostgresDataRepository Repository { get; }

    internal SearchRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    public async Task<IList<PredefinedSearchHeader>> GetPredefinedSearchHeadersAsync()
    {
        var predefinedSearchHeaders = await Repository.Database.PredefinedSearches.OrderBy(d => d.Name).Select(d => new PredefinedSearchHeader
        {
            Name = d.Name,
            Uri = d.Uri,
            BuiltIn = d.BuiltIn,
            Created = d.Created,
            Id = d.Id,
            MetaverseAttributeCount = d.Attributes.Count(),
            MetaverseObjectTypeName = d.MetaverseObjectType.Name,
            IsDefaultForMetaverseObjectType = d.IsDefaultForMetaverseObjectType
        }).ToListAsync();

        return predefinedSearchHeaders;
    }

    public async Task<PredefinedSearch?> GetPredefinedSearchAsync(string uri)
    {
        return await Repository.Database.PredefinedSearches.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(q => q.Attributes).
            ThenInclude(q => q.MetaverseAttribute).
            Include(q => q.MetaverseObjectType).
            Include(q => q.CriteriaGroups).
            ThenInclude(cg => cg.Criteria).
            ThenInclude(c => c.MetaverseAttribute).
            SingleOrDefaultAsync(q => q.Uri == uri);
    }

    public async Task<PredefinedSearch?> GetPredefinedSearchAsync(MetaverseObjectType metaverseObjectType)
    {
        return await Repository.Database.PredefinedSearches.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(q => q.Attributes).
            ThenInclude(a => a.MetaverseAttribute).
            Include(q => q.MetaverseObjectType).
            Include(q => q.CriteriaGroups).
            ThenInclude(cg => cg.Criteria).
            ThenInclude(c => c.MetaverseAttribute).
            SingleOrDefaultAsync(q => q.MetaverseObjectType.Id == metaverseObjectType.Id && q.IsDefaultForMetaverseObjectType);
    }
}
