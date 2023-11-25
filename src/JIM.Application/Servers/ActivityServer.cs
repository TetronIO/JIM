using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Utility;

namespace JIM.Application.Servers
{
    public class ActivityServer
    {
        private JimApplication Application { get; }

        internal ActivityServer(JimApplication application)
        {
            Application = application;
        }

        public async Task CreateActivityAsync(Activity activity, MetaverseObject? initiatedBy)
        {
            activity.Status = ActivityStatus.InProgress;
            activity.Executed = DateTime.UtcNow;

            if (initiatedBy != null)
            {
                activity.InitiatedBy = initiatedBy;
                activity.InitiatedByName = initiatedBy.DisplayName;
            }

            if (activity.TargetType == ActivityTargetType.ConnectedSystemRunProfile)
            {
                if (activity.RunProfile == null)
                    throw new InvalidDataException("Activity.RunProfile has not been set. Cannot continue.");

                // we want to retain some basic info about run profiles when they're being deleted
                activity.TargetName = activity.RunProfile.Name;
                if (activity.TargetOperationType == ActivityTargetOperationType.Delete)
                    activity.RunType = activity.RunProfile.RunType;
            } 
            else if (activity.TargetType == ActivityTargetType.ConnectedSystem)
            {
                if (activity.ConnectedSystemId == null && activity.TargetOperationType != ActivityTargetOperationType.Create)
                    throw new InvalidDataException("Activity.ConnectedSysetmId has not been set and must be for UPDATE and DELETE operations. Cannot continue.");
            }

            await Application.Repository.Activity.CreateActivityAsync(activity);
        }

        public async Task CompleteActivityAsync(Activity activity)
        {
            var now = DateTime.UtcNow;
            activity.Status = ActivityStatus.Complete;
            activity.ExecutionTime = now - activity.Executed;
            activity.TotalActivityTime = now - activity.Created;
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task CompleteActivityWithError(Activity activity, Exception exception)
        {
            var now = DateTime.UtcNow;
            activity.ExecutionTime = DateTime.UtcNow - activity.Executed;
            activity.ErrorMessage = exception.Message;
            activity.ErrorStackTrace = exception.StackTrace;
            activity.ExecutionTime = now - activity.Executed;
            activity.TotalActivityTime = now - activity.Created;
            activity.Status = ActivityStatus.CompleteWithError;
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task FailActivityWithErrorAsync(Activity activity, Exception exception)
        {
            var now = DateTime.UtcNow;
            activity.ErrorMessage = exception.Message;
            activity.ErrorStackTrace = exception.StackTrace;
            activity.Status = ActivityStatus.FailedWithError;
            activity.ExecutionTime = now - activity.Executed;
            activity.TotalActivityTime = now - activity.Created;
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task CancelActivityAsync(Activity activity)
        {
            if (activity.Status == ActivityStatus.Cancelled)
                return;
            
            var now = DateTime.UtcNow;
            activity.ExecutionTime = now - activity.Executed;
            activity.TotalActivityTime = now - activity.Created;
            activity.Status = ActivityStatus.Cancelled;
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task UpdateActivityAsync(Activity activity)
        {
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task DeleteActivityAsync(Activity activity)
        {
            await Application.Repository.Activity.DeleteActivityAsync(activity);
        }

        public async Task<Activity?> GetActivityAsync(Guid id)
        {
            return await Application.Repository.Activity.GetActivityAsync(id);
        }

        /// <summary>
        /// Retrieves a page's worth of top-level activities, i.e. those that do not have a parent activity.
        /// </summary>
        public async Task<PagedResultSet<Activity>> GetActivitiesAsync(int page = 1, int pageSize = 20, int maxResults = 500, QuerySortBy querySortBy = QuerySortBy.DateCreated)
        {
            return await Application.Repository.Activity.GetActivitiesAsync(page, pageSize, maxResults, querySortBy);
        }
    }
}
