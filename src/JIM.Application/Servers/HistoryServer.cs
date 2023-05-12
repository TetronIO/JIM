using JIM.Models.History;
using Serilog;

namespace JIM.Application.Servers
{
    public class HistoryServer
    {
        private JimApplication Application { get; }

        internal HistoryServer(JimApplication application)
        {
            Application = application;
        }

        public async Task RecordSynchronisationRunAsync(SynchronisationRunHistoryDetail synchronisationRunHistoryDetail)
        {
            if (synchronisationRunHistoryDetail == null)
                throw new ArgumentNullException(nameof(synchronisationRunHistoryDetail));

            if (synchronisationRunHistoryDetail.RunHistoryItem != null)
                throw new ArgumentException($"{nameof(synchronisationRunHistoryDetail)} already has a RunHistoryItem associated. This is not supported.");

            // create the detail object
            await Application.Repository.History.CreateSynchronisationRunHistoryDetailAsync(synchronisationRunHistoryDetail);
            Log.Verbose($"RecordSynchronisationRunAsync: Created a detail object for: {synchronisationRunHistoryDetail.RunProfileName}");

            // then create the history object
            var runHistoryItem = new RunHistoryItem(synchronisationRunHistoryDetail);
            await Application.Repository.History.CreateRunHistoryItemAsync(runHistoryItem);
            Log.Verbose($"RecordSynchronisationRunAsync: Created the corresponding run history item for run profile: {synchronisationRunHistoryDetail.RunProfileName}");
        }

        public async Task UpdateSynchronisationRunAsync(SynchronisationRunHistoryDetail synchronisationRunHistoryDetail)
        {
            await Application.Repository.History.UpdateSynchronisationRunHistoryDetailAsync(synchronisationRunHistoryDetail);
            Log.Verbose($"RecordSynchronisationRunAsync: Updated SynchronisationRunHistoryDetail for {synchronisationRunHistoryDetail.RunProfileName}");
        }
    }
}
