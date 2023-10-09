namespace JIM.Models.History
{
    public class RunHistoryItem : HistoryItem
    {
        public Guid? SynchronisationRunHistoryDetailId { get; set; }

        public RunHistoryItem()
        {
            // Entity Framework uses this constructor when retrieving objects from the database
        }
    }
}
