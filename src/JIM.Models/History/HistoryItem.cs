using JIM.Models.Core;
using JIM.Models.Enums;

namespace JIM.Models.History
{
    public abstract class HistoryItem
    {
        public Guid Id { get; set; }

        public DateTime Created { get; set; }

        public MetaverseObject? InitiatedBy { get; set; }

        public string? InitiatedByName { get; set; }

        public string? ErrorMessage { get; set; }

        public string? ErrorStackTrace { get; set; }

        /// <summary>
        /// When the task is complete, a value for how long the task took to complete should be stored here.
        /// </summary>
        public TimeSpan? CompletionTime { get; set; }

        public HistoryStatus Status { get; set; }

        public HistoryItem()
        {
            Created = DateTime.Now;
        }
    }
}
