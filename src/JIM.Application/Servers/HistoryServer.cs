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

        public async Task CreateSyncRunHistoryDetailAsync(SyncRunHistoryDetail syncRunHistoryDetail)
        {
            if (syncRunHistoryDetail == null)
                throw new ArgumentNullException(nameof(syncRunHistoryDetail));

            if (syncRunHistoryDetail.RunHistoryItem != null)
                throw new ArgumentException($"{nameof(syncRunHistoryDetail)} already has a RunHistoryItem associated. This is not supported.");

            // create the detail object
            await Application.Repository.History.CreateSyncRunHistoryDetailAsync(syncRunHistoryDetail);
            
            // then we're able to create the history object
            var runHistoryItem = new RunHistoryItem(syncRunHistoryDetail);
            await Application.Repository.History.CreateRunHistoryItemAsync(runHistoryItem);
        }

        public async Task UpdateSyncRunHistoryDetailAsync(SyncRunHistoryDetail syncRunHistoryDetail)
        {
            if (syncRunHistoryDetail == null)
                throw new ArgumentNullException(nameof(syncRunHistoryDetail));

            await Application.Repository.History.UpdateSyncRunHistoryDetailAsync(syncRunHistoryDetail);
        }
    }
}
