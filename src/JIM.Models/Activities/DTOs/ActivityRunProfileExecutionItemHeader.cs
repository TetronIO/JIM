using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Activities.DTOs
{
    public class ActivityRunProfileExecutionItemHeader
    {
        public Guid Id { get; set; }

        public string? DisplayName { get; set; }

        public ObjectChangeType ObjectChangeType { get; set; }

        public ConnectedSystemObjectTypeAttribute UniqueIdentifierAttribute { get; set ;} = new ConnectedSystemObjectTypeAttribute();

        public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; }
    }
}
