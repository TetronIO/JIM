// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Preview;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

public class ConfigurationChangePreviewRepository : IConfigurationChangePreviewRepository
{
    /// <summary>
    /// The escape character declared to `ILIKE`, so a `%` or `_` an administrator typed is matched as the character
    /// they typed rather than as a wildcard. A search for "100%" that quietly returned the whole group would be a
    /// worse answer than no results at all, because it looks like one.
    /// </summary>
    private const string LikeEscapeCharacter = "\\";

    private readonly JimDbContext _database;

    public ConfigurationChangePreviewRepository(JimDbContext database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task CreatePreviewAsync(ConfigurationChangePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        // Entry(), not Add(): Add walks every navigation and marks each untracked entity it reaches for insertion,
        // and the preview's Activity is already persisted. Reaching it would fail the insert on a duplicate key,
        // naming the Activities table from a call that was only ever about a preview row.
        _database.Entry(preview).State = EntityState.Added;
        await _database.SaveChangesAsync();
    }

    public async Task UpdatePreviewAsync(ConfigurationChangePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        // As above, and for the same reason; here it also confines the write to the preview's own columns, leaving
        // whatever groups and deltas are hanging off the in-memory instance alone.
        _database.Entry(preview).State = EntityState.Modified;
        await _database.SaveChangesAsync();
    }

    public async Task<ConfigurationChangePreview?> GetPreviewAsync(Guid activityId) =>
        await _database.ConfigurationChangePreviews.SingleOrDefaultAsync(p => p.ActivityId == activityId);

    public async Task<List<ConfigurationChangePreviewGroup>> GetPreviewGroupsAsync(Guid activityId) =>
        await _database.ConfigurationChangePreviewGroups
            .Where(g => g.ActivityId == activityId)
            .OrderByDescending(g => g.ObjectCount)
            .ThenBy(g => g.Id)
            .ToListAsync();

    public async Task CreatePreviewResultsAsync(IReadOnlyCollection<ConfigurationChangePreviewGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        if (groups.Count == 0)
            return;

        // AddRange traverses each group's Deltas collection and inserts them with it, which is what is wanted here:
        // the deltas are new rows created alongside their group, and EF fills in the group foreign key that could
        // not be known before the group had an id. The one navigation that must stay unset is Preview, for the
        // same graph-walking reason as above; the orchestrator sets ActivityId only.
        _database.ConfigurationChangePreviewGroups.AddRange(groups);
        await _database.SaveChangesAsync();
    }

    public async Task<PagedResultSet<ConfigurationChangePreviewDelta>> GetPreviewDeltasAsync(Guid activityId, Guid? groupId, int page, int pageSize,
        string? search = null)
    {
        if (page < 1)
            page = 1;
        if (pageSize < 1)
            pageSize = 20;

        var query = _database.ConfigurationChangePreviewDeltas.Where(d => d.ActivityId == activityId);
        if (groupId.HasValue)
        {
            var group = groupId.Value;
            query = query.Where(d => d.GroupId == group);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Searched across the columns the drill-down actually renders, because the administrator typing here is
            // looking for an object by whatever they happen to know about it: its name, the attribute concerned, or
            // the value it would move to or from.
            var pattern = $"%{EscapeLikeWildcards(search)}%";
            query = query.Where(d =>
                (d.ObjectDisplayName != null && EF.Functions.ILike(d.ObjectDisplayName, pattern, LikeEscapeCharacter)) ||
                (d.AttributeName != null && EF.Functions.ILike(d.AttributeName, pattern, LikeEscapeCharacter)) ||
                (d.OldValue != null && EF.Functions.ILike(d.OldValue, pattern, LikeEscapeCharacter)) ||
                (d.NewValue != null && EF.Functions.ILike(d.NewValue, pattern, LikeEscapeCharacter)));
        }

        // Counted after filtering, so the pager sizes itself to the matches rather than to the group.
        var totalResults = await query.CountAsync();

        // Ordered by display name then id: paging over an unordered set silently repeats and omits rows between
        // pages, and a drill-down that quietly omits objects is exactly the thing a preview exists to prevent.
        var results = await query
            .OrderBy(d => d.ObjectDisplayName)
            .ThenBy(d => d.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResultSet<ConfigurationChangePreviewDelta>
        {
            Results = results,
            TotalResults = totalResults,
            CurrentPage = page,
            PageSize = pageSize
        };
    }

    private static string EscapeLikeWildcards(string search) =>
        search.Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter)
            .Replace("%", LikeEscapeCharacter + "%")
            .Replace("_", LikeEscapeCharacter + "_");
}
