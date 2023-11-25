namespace JIM.Models.Activities;

public enum ActivityStatus
{
    NotSet = 0,
    InProgress = 1,
    Complete = 2,
    CompleteWithError = 3,
    FailedWithError = 4,
    Cancelled = 5
}
