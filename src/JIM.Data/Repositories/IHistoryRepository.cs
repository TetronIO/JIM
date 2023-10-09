using JIM.Models.History;

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
    }
}
