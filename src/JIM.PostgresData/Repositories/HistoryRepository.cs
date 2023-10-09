using JIM.Data.Repositories;
using JIM.Models.History;
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
    }
}
