using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Utility;
namespace JIM.Data.Repositories;

public interface IActivityRepository
{

    public Task CreateActivityAsync(Activity activity);

    public Task UpdateActivityAsync(Activity activity);

    public Task DeleteActivityAsync(Activity activity);

    public Task<Activity?> GetActivityAsync(Guid id);

    public Task<PagedResultSet<Activity>> GetActivitiesAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null);

    public Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(Guid activityId, int page, int pageSize);

    public Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId);

    public Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id);
}