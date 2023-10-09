using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.History;

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
    }
}
