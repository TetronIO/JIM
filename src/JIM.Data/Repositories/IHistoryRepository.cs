using JIM.Models.History;

namespace JIM.Data.Repositories
{
    public interface IHistoryRepository
    {
        public Task CreateSynchronisationRunHistoryDetailAsync(SynchronisationRunHistoryDetail synchronisationRunHistoryDetail);

        public Task CreateRunHistoryItemAsync(RunHistoryItem runHistoryItem);

        public Task UpdateSynchronisationRunHistoryDetailAsync(SynchronisationRunHistoryDetail synchronisationRunHistoryDetail);
    }
}
