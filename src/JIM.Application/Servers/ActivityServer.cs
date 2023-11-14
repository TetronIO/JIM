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

            if (initiatedBy != null)
            {
                activity.InitiatedBy = initiatedBy;
                activity.InitiatedByName = initiatedBy.DisplayName;
            }

            await Application.Repository.Activity.CreateActivityAsync(activity);
        }

        public async Task CompleteActivityAsync(Activity activity)
        {
            activity.Status = ActivityStatus.Complete;
            activity.CompletionTime = activity.Created - DateTime.UtcNow;
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task CompleteActivityWithError(Activity activity, Exception exception)
        {
            activity.CompletionTime = activity.Created - DateTime.UtcNow;
            activity.ErrorMessage = exception.Message;
            activity.ErrorStackTrace = exception.StackTrace;
            activity.Status = ActivityStatus.CompleteWithError;
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task FailActivityWithError(Activity activity, Exception exception)
        {
            activity.ErrorMessage = exception.Message;
            activity.ErrorStackTrace = exception.StackTrace;
            activity.Status = ActivityStatus.FailedWithError;
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task UpdateActivity(Activity activity)
        {
            await Application.Repository.Activity.UpdateActivityAsync(activity);
        }

        public async Task DeleteActivityAsync(Activity activity)
        {
            await Application.Repository.Activity.DeleteActivityAsync(activity);
        }

        public async Task<PagedResultSet<Activity>> GetActivitiesAsync(int page = 1, int pageSize = 20, int maxResults = 500, QuerySortBy querySortBy = QuerySortBy.DateCreated)
        {
            return await Application.Repository.Activity.GetActivitiesAsync(page, pageSize, maxResults, querySortBy);
        }
    }
}
