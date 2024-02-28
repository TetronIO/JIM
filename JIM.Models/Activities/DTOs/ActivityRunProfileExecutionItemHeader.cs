using JIM.Models.Enums;

namespace JIM.Models.Activities.DTOs
{
    public class ActivityRunProfileExecutionItemHeader
    {
        public Guid Id { get; set; }

        public string? ExternalIdValue { get; set; }

        public string? DisplayName { get; set; }

        public string? ConnectedSystemObjectType { get; set; }

        public ObjectChangeType ObjectChangeType { get; set; }

        public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; }
    }
}
