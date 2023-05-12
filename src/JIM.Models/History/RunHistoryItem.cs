namespace JIM.Models.History
{
    public class RunHistoryItem : HistoryItem
    {
        public Guid? SynchronisationRunHistoryDetailId { get; set; }

        public RunHistoryItem()
        {
        }

        public RunHistoryItem(SynchronisationRunHistoryDetail synchronisationRunHistoryDetail)
        {
            SynchronisationRunHistoryDetailId = synchronisationRunHistoryDetail.Id;
        }
    }
}
