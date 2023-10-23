using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Models.Utility;

namespace JIM.Application.Servers
{
    public class HistoryServer
    {
        private JimApplication Application { get; }

        internal HistoryServer(JimApplication application)
        {
            Application = application;
        }

        public async Task<RunHistoryItem> CreateSyncRunHistoryDetailAsync(SyncRunHistoryDetail syncRunHistoryDetail, MetaverseObject? initiatedBy)
        {
            if (syncRunHistoryDetail == null)
                throw new ArgumentNullException(nameof(syncRunHistoryDetail));

            if (syncRunHistoryDetail.RunHistoryItem != null)
                throw new ArgumentException($"{nameof(syncRunHistoryDetail)} already has a RunHistoryItem associated. This is not supported.");

            // create the required child detail object
            await Application.Repository.History.CreateSyncRunHistoryDetailAsync(syncRunHistoryDetail);

            // then we're able to link the detail object to the history object
            var runHistoryItem = new RunHistoryItem
            {
                SynchronisationRunHistoryDetailId = syncRunHistoryDetail.Id,
                Status = HistoryStatus.InProgress
            };

            if (initiatedBy != null)
            {
                runHistoryItem.InitiatedBy = initiatedBy;
                runHistoryItem.InitiatedByName = initiatedBy.DisplayName;
            }

            await Application.Repository.History.CreateRunHistoryItemAsync(runHistoryItem);
            return runHistoryItem;
        }

        public async Task UpdateSyncRunHistoryDetailAsync(SyncRunHistoryDetail syncRunHistoryDetail)
        {
            if (syncRunHistoryDetail == null)
                throw new ArgumentNullException(nameof(syncRunHistoryDetail));

            await Application.Repository.History.UpdateSyncRunHistoryDetailAsync(syncRunHistoryDetail);
        }

        public async Task UpdateRunHistoryItemAsync(RunHistoryItem runHistoryItem)
        {
            if (runHistoryItem == null)
                throw new ArgumentNullException(nameof(runHistoryItem));

            await Application.Repository.History.UpdateRunHistoryItemAsync(runHistoryItem);
        }

        public async Task<ClearConnectedSystemHistoryItem> CreateClearConnectedSystemHistoryItemAsync(int connectedSystemId, MetaverseObject? initiatedBy)
        {
            // create the history object
            var clearConnectedSystemHistoryItem = new ClearConnectedSystemHistoryItem(connectedSystemId) { Status = HistoryStatus.InProgress };

            if (initiatedBy != null)
            {
                clearConnectedSystemHistoryItem.InitiatedBy = initiatedBy;
                clearConnectedSystemHistoryItem.InitiatedByName = initiatedBy.DisplayName;
            }

            await Application.Repository.History.CreateClearConnectedSystemHistoryItemAsync(clearConnectedSystemHistoryItem);
            return clearConnectedSystemHistoryItem;
        }

        public async Task UpdateClearConnectedSystemHistoryItemAsync(ClearConnectedSystemHistoryItem clearConnectedSystemHistoryItem)
        {
            await Application.Repository.History.UpdateClearConnectedSystemHistoryItemAsync(clearConnectedSystemHistoryItem);
        }

        public async Task<DataGenerationHistoryItem> CreateDataGenerationHistoryItemAsync(int dataGenerationTemplateId, MetaverseObject? initiatedBy)
        {
            // create the history object
            var dataGenerationHistoryItem = new DataGenerationHistoryItem(dataGenerationTemplateId) { Status = HistoryStatus.InProgress };

            if (initiatedBy != null)
            {
                dataGenerationHistoryItem.InitiatedBy = initiatedBy;
                dataGenerationHistoryItem.InitiatedByName = initiatedBy.DisplayName;
            }

            await Application.Repository.History.CreateDataGenerationHistoryItemAsync(dataGenerationHistoryItem);
            return dataGenerationHistoryItem;
        }

        public async Task UpdateDataGenerationHistoryItemAsync(DataGenerationHistoryItem dataGenerationHistoryItem)
        {
            await Application.Repository.History.UpdateDataGenerationHistoryItemAsync(dataGenerationHistoryItem);
        }

        public async Task<PagedResultSet<HistoryItem>> GetHistoryItemsAsync(int page = 1, int pageSize = 20, int maxResults = 500, QuerySortBy querySortBy = QuerySortBy.DateCreated)
        {
            return await Application.Repository.History.GetHistoryItemsAsync(page, pageSize, maxResults, querySortBy);
        }
    }
}
