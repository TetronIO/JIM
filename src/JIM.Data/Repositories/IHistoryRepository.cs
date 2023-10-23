using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Models.Utility;

namespace JIM.Data.Repositories
{
    public interface IHistoryRepository
    {
        public Task CreateSyncRunHistoryDetailAsync(SyncRunHistoryDetail synchronisationRunHistoryDetail);

        public Task CreateRunHistoryItemAsync(RunHistoryItem runHistoryItem);

        public Task UpdateSyncRunHistoryDetailAsync(SyncRunHistoryDetail synchronisationRunHistoryDetail);

        public Task UpdateRunHistoryItemAsync(RunHistoryItem runHistoryItem);

        public Task CreateClearConnectedSystemHistoryItemAsync(ClearConnectedSystemHistoryItem clearConnectedSystemHistoryItem);

        public Task UpdateClearConnectedSystemHistoryItemAsync(ClearConnectedSystemHistoryItem clearConnectedSystemHistoryItem);

        public Task CreateDataGenerationHistoryItemAsync(DataGenerationHistoryItem dataGenerationHistoryItem);

        public Task UpdateDataGenerationHistoryItemAsync(DataGenerationHistoryItem dataGenerationHistoryItem);

        public Task<PagedResultSet<HistoryItem>> GetHistoryItemsAsync(int page, int pageSize, int maxResults, QuerySortBy querySortBy);
    }
}
