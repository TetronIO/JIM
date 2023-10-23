using JIM.Data.Repositories;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories
{
    public class HistoryRepository : IHistoryRepository
    {
        private PostgresDataRepository Repository { get; }

        internal HistoryRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public async Task CreateSyncRunHistoryDetailAsync(SyncRunHistoryDetail synchronisationRunHistoryDetail)
        {
            Repository.Database.SyncRunHistoryDetails.Add(synchronisationRunHistoryDetail);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task CreateRunHistoryItemAsync(RunHistoryItem runHistoryItem)
        {
            Repository.Database.RunHistoryItems.Add(runHistoryItem);
            await Repository.Database.SaveChangesAsync();
        }
        
        public async Task CreateClearConnectedSystemHistoryItemAsync(ClearConnectedSystemHistoryItem clearConnectedSystemHistoryItem)
        {
            // weird: we've seen issues where EF thinks the initiatedBy user was not retrieved from this JIM instance
            // so for now just re-retrieve the user here.

            if (clearConnectedSystemHistoryItem.InitiatedBy == null)
                throw new InvalidDataException("CreateClearConnectedSystemHistoryItemAsync: clearConnectedSystemHistoryItem.InitiatedBy was null. Cannot continue.");

            var localInitiatedBy = await Repository.Database.MetaverseObjects.SingleOrDefaultAsync(m => m.Id == clearConnectedSystemHistoryItem.InitiatedBy.Id);
            clearConnectedSystemHistoryItem.InitiatedBy = localInitiatedBy;

            Repository.Database.ClearConnectedSystemHistoryItems.Add(clearConnectedSystemHistoryItem);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateSyncRunHistoryDetailAsync(SyncRunHistoryDetail synchronisationRunHistoryDetail)
        {
            Repository.Database.SyncRunHistoryDetails.Update(synchronisationRunHistoryDetail);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateRunHistoryItemAsync(RunHistoryItem runHistoryItem)
        {
            Repository.Database.RunHistoryItems.Update(runHistoryItem);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateClearConnectedSystemHistoryItemAsync(ClearConnectedSystemHistoryItem clearConnectedSystemHistoryItem)
        {
            Repository.Database.ClearConnectedSystemHistoryItems.Update(clearConnectedSystemHistoryItem);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task CreateDataGenerationHistoryItemAsync(DataGenerationHistoryItem dataGenerationHistoryItem)
        {
            Repository.Database.DataGenerationHistoryItems.Update(dataGenerationHistoryItem);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateDataGenerationHistoryItemAsync(DataGenerationHistoryItem dataGenerationHistoryItem)
        {
            Repository.Database.DataGenerationHistoryItems.Update(dataGenerationHistoryItem);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task<PagedResultSet<HistoryItem>> GetHistoryItemsAsync(
            int page, 
            int pageSize, 
            int maxResults,
            QuerySortBy querySortBy = QuerySortBy.DateCreated)
        {
            if (pageSize < 1)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

            if (page < 1)
                page = 1;

            // limit page size to avoid increasing latency unecessarily
            if (pageSize > 100)
                pageSize = 100;

            // limit how big the id query is to avoid unnecessary charges and to keep latency within an acceptable range
            if (maxResults > 500)
                maxResults = 500;

            var objects = from o in Repository.Database.HistoryItems
                          select o;

            switch (querySortBy)
            {
                case QuerySortBy.DateCreated:
                    objects = objects.OrderByDescending(q => q.Created);
                    break;

                // todo: support more ways of sorting, i.e. by attribute value
            }

            // now just retrieve a page's worth of images from the results
            var grossCount = objects.Count();
            var offset = (page - 1) * pageSize;
            var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
            var results = await objects.Skip(offset).Take(itemsToGet).ToListAsync();

            // now with all the ids we know how many total results there are and so can populate paging info
            var pagedResultSet = new PagedResultSet<HistoryItem>
            {
                PageSize = pageSize,
                TotalResults = grossCount,
                CurrentPage = page,
                QuerySortBy = querySortBy,
                Results = results
            };

            if (page == 1 && pagedResultSet.TotalPages == 0)
                return pagedResultSet;

            // don't let users try and request a page that doesn't exist
            if (page > pagedResultSet.TotalPages)
            {
                pagedResultSet.TotalResults = 0;
                pagedResultSet.Results.Clear();
                return pagedResultSet;
            }

            return pagedResultSet;
        }
    }
}
