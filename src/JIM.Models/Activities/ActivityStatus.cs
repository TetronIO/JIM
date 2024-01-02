namespace JIM.Models.Activities;

public enum ActivityStatus
{
    NotSet = 0,
    InProgress = 1,
    Complete = 2,
    CompleteWithWarning = 3,
    CompleteWithError = 4,
    FailedWithError = 5,
    Cancelled = 6
}
