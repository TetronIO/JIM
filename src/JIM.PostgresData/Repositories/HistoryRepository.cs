using JIM.Data.Repositories;
using JIM.Models.History;

namespace JIM.PostgresData.Repositories
{
    public class HistoryRepository : IHistoryRepository
    {
        private PostgresDataRepository Repository { get; }

        internal HistoryRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public async Task CreateSynchronisationRunHistoryDetailAsync(SynchronisationRunHistoryDetail synchronisationRunHistoryDetail)
        {
            Repository.Database.SynchronisationRunHistoryDetails.Add(synchronisationRunHistoryDetail);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task CreateRunHistoryItemAsync(RunHistoryItem runHistoryItem)
        {
            Repository.Database.RunHistoryItems.Add(runHistoryItem);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateSynchronisationRunHistoryDetailAsync(SynchronisationRunHistoryDetail synchronisationRunHistoryDetail)
        {
            Repository.Database.SynchronisationRunHistoryDetails.Update(synchronisationRunHistoryDetail);
            await Repository.Database.SaveChangesAsync();
        }
    }
}
