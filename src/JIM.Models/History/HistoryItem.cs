using JIM.Models.Core;

namespace JIM.Models.History
{
    public abstract class HistoryItem
    {
        public Guid Id { get; set; }

        public DateTime Created { get; set; }

        public MetaverseObject? InitiatedBy { get; set; }

        public string? InitiatedByName { get; set; }

        public HistoryItem()
        {
            Created = DateTime.MinValue;
        }
    }
}
