// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Text;
using System.Text.Json;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Utility;

namespace JIM.Application.Servers;

/// <summary>
/// The downward causal walk (#1811): what a Run Profile Execution Item went on to cause.
/// </summary>
public partial class ActivityServer
{
    /// <summary>
    /// How many members of each cohort the walk returns; the rest are paged by cohort key.
    /// </summary>
    public const int CausalConsequenceMemberPageSize = 50;

    /// <summary>
    /// How many items the walk carries from one level into the next, across all cohorts. Past it the branches read as
    /// going further than shown rather than loading more: counts stay exact, only the continuation is bounded.
    /// </summary>
    public const int CausalConsequenceContinuingItemLimit = 10_000;

    /// <summary>
    /// The longest cohort path a key may encode, comfortably past any depth the walk is asked for. Stops a crafted key
    /// from making one request issue an unbounded number of reads.
    /// </summary>
    private const int MaxCohortKeyDepth = 16;

    private static readonly JsonSerializerOptions CohortKeyJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Walks downward from a Run Profile Execution Item: what the changes it describes went on to cause, as a tree of
    /// cohorts, with every branch ending in a named state (#1811).
    /// </summary>
    /// <param name="runProfileExecutionItemId">The item to start from.</param>
    /// <param name="maxDepth">How many levels to follow. The walk reads one level past it to tell a branch that goes
    /// further from one that happens to end there, so the bound is never reported where nothing lies beyond it.</param>
    /// <remarks>
    /// One repository read per level, however many members a level holds: each read finds, groups and counts a whole
    /// level in the database. Consequences can still be happening, so the result is a reading taken now, and says
    /// whether any branch is waiting on something that will happen without anyone acting.
    /// </remarks>
    public async Task<CausalConsequences> GetCausalConsequencesAsync(Guid runProfileExecutionItemId, int maxDepth = 6)
    {
        var rootCohorts = new List<CausalConsequenceCohort>();
        var rootEndings = new List<CausalConsequenceEndingCount>();
        var truncatedByDepth = false;

        // The cohorts at the previous level, by the ordinal the read gave them; the root is its own parent, 0.
        var parents = new Dictionary<int, ConsequenceParent> { [0] = new(null, []) };
        List<CausalConsequenceFrontierMember> frontier = [new(runProfileExecutionItemId, 0)];

        for (var depth = 1; frontier.Count > 0; depth++)
        {
            var level = await Application.Repository.Activity.GetCausalConsequenceLevelAsync(
                frontier, CausalConsequenceMemberPageSize, CausalConsequenceContinuingItemLimit);

            // Leaves are true ends at any depth, the bound check included.
            foreach (var leaf in level.Leaves)
            {
                var endings = parents.TryGetValue(leaf.ParentCohortOrdinal, out var leafParent) && leafParent.Cohort != null
                    ? leafParent.Cohort.Endings
                    : rootEndings;
                endings.Add(new CausalConsequenceEndingCount
                {
                    Ending = leaf.Ending,
                    MemberCount = leaf.MemberCount,
                    ConnectedSystemId = leaf.ConnectedSystemId,
                    ConnectedSystemName = leaf.ConnectedSystemName
                });
            }

            if (depth > maxDepth)
            {
                // The level past the bound is read only to learn which branches go further.
                foreach (var row in level.Cohorts)
                {
                    if (parents.TryGetValue(row.ParentCohortOrdinal, out var boundParent) && boundParent.Cohort != null)
                        boundParent.Cohort.IsFurtherThanShown = true;
                    truncatedByDepth = true;
                }

                break;
            }

            var nextParents = new Dictionary<int, ConsequenceParent>();
            var nextFrontier = new List<CausalConsequenceFrontierMember>();
            foreach (var row in level.Cohorts)
            {
                if (!parents.TryGetValue(row.ParentCohortOrdinal, out var parent))
                    continue;

                List<CausalConsequenceCohortTuple> path = [.. parent.Path, row.Tuple];
                var cohort = BuildConsequenceCohort(row, EncodeCohortKey(path));
                (parent.Cohort?.Consequences ?? rootCohorts).Add(cohort);
                nextParents[row.Ordinal] = new ConsequenceParent(cohort, path);

                // Carried on within the level's budget; anything the read or the budget leaves behind is said, not lost.
                var budget = CausalConsequenceContinuingItemLimit - nextFrontier.Count;
                var carried = row.ContinuingItemIds.Take(Math.Max(budget, 0)).ToList();
                nextFrontier.AddRange(carried.Select(id => new CausalConsequenceFrontierMember(id, row.Ordinal)));
                if (carried.Count < row.ContinuingMemberCount)
                    cohort.IsFurtherThanShown = true;
            }

            parents = nextParents;
            frontier = nextFrontier;
        }

        await AttachRepresentativesAsync(rootCohorts);

        return new CausalConsequences
        {
            RunProfileExecutionItemId = runProfileExecutionItemId,
            Cohorts = rootCohorts,
            Endings = rootEndings,
            IsTruncatedByDepth = truncatedByDepth,
            HasWaitingBranches = rootEndings.Any(e => IsWaiting(e.Ending)) || rootCohorts.Any(HasWaitingBranch),
            CheckedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Pages through one cohort's members (#1811), found from the item the walk started at by the cohort's key.
    /// </summary>
    /// <param name="runProfileExecutionItemId">The item the walk started from.</param>
    /// <param name="cohortKey">The cohort's <see cref="CausalConsequenceCohort.CohortKey"/>.</param>
    /// <param name="page">The page to return, from 1.</param>
    /// <param name="pageSize">How many members per page.</param>
    /// <param name="search">Text a member's name must contain, or null for every member.</param>
    /// <returns>The page, or null when the key does not decode to a cohort path.</returns>
    /// <remarks>
    /// Stateless: the key is the cohort's path of shared values from the root, and the server re-derives the cohort by
    /// following it, narrowing to each cohort on the path in turn, one read per level. Each level carries on the same
    /// members, in the same order and to the same bound, as the walk did, so the pages are the cohort the walk showed.
    /// </remarks>
    public async Task<PagedResultSet<CausalConsequenceMember>?> GetCausalConsequenceCohortMembersAsync(
        Guid runProfileExecutionItemId, string cohortKey, int page, int pageSize, string? search = null)
    {
        var path = TryDecodeCohortKey(cohortKey);
        if (path == null)
            return null;

        page = Math.Max(page, 1);
        pageSize = Math.Max(pageSize, 1);
        IReadOnlyList<Guid> frontier = [runProfileExecutionItemId];
        for (var i = 0; i < path.Count - 1 && frontier.Count > 0; i++)
        {
            var (continuing, _) = await Application.Repository.Activity.GetCausalConsequenceCohortMembersAsync(
                frontier, path[i], 0, CausalConsequenceContinuingItemLimit, search: null);
            frontier = continuing.Where(m => m.RunProfileExecutionItemId.HasValue).Select(m => m.RunProfileExecutionItemId!.Value).ToList();
        }

        List<CausalConsequenceMember> members = [];
        var total = 0;
        if (frontier.Count > 0)
        {
            (members, total) = await Application.Repository.Activity.GetCausalConsequenceCohortMembersAsync(
                frontier, path[^1], (page - 1) * pageSize, pageSize, search);
        }

        return new PagedResultSet<CausalConsequenceMember>
        {
            Results = members,
            TotalResults = total,
            PageSize = pageSize,
            CurrentPage = page
        };
    }

    /// <summary>
    /// Gives each cohort of items its first member in full (<see cref="CausalConsequenceCohort.Representative"/>), in one
    /// read for the whole walk however many cohorts and levels it found.
    /// </summary>
    private async Task AttachRepresentativesAsync(List<CausalConsequenceCohort> rootCohorts)
    {
        var representativeIds = new Dictionary<CausalConsequenceCohort, Guid>();
        var pending = new Stack<CausalConsequenceCohort>(rootCohorts);
        while (pending.Count > 0)
        {
            var cohort = pending.Pop();
            if (cohort.Members.FirstOrDefault(m => m.RunProfileExecutionItemId.HasValue)?.RunProfileExecutionItemId is { } itemId)
                representativeIds[cohort] = itemId;
            foreach (var consequence in cohort.Consequences)
                pending.Push(consequence);
        }

        if (representativeIds.Count == 0)
            return;

        var items = await Application.Repository.Activity.GetCausalConsequenceRepresentativesAsync(representativeIds.Values.Distinct().ToList());
        foreach (var (cohort, itemId) in representativeIds)
            cohort.Representative = items.GetValueOrDefault(itemId);
    }

    /// <summary>
    /// Builds a cohort from its row, naming where its branch ends when its hop kind ends one.
    /// </summary>
    private static CausalConsequenceCohort BuildConsequenceCohort(CausalConsequenceCohortRow row, string cohortKey)
    {
        var tuple = row.Tuple;
        return new CausalConsequenceCohort
        {
            CohortKey = cohortKey,
            HopKind = tuple.HopKind,
            EdgeType = tuple.EdgeType,
            ReasonCode = tuple.ReasonCode,
            ConnectedSystemId = tuple.ConnectedSystemId,
            ConnectedSystemName = row.ConnectedSystemName,
            SyncRuleId = tuple.SyncRuleId,
            SyncRuleName = row.SyncRuleName,
            ObjectTypeName = tuple.ObjectTypeName,
            ObjectTypePluralName = row.ObjectTypePluralName,
            AttributeName = tuple.AttributeName,
            ChangeType = tuple.ChangeType,
            OutcomeSummary = tuple.OutcomeSummary,
            PendingExportStatus = tuple.PendingExportStatus,
            MemberCount = row.MemberCount,
            FirstOccurred = row.FirstOccurred,
            LastOccurred = row.LastOccurred,
            DueAt = row.DueAt,
            Members = row.Members,
            Ending = tuple.HopKind switch
            {
                CausalConsequenceHopKind.Confirmation => CausalConsequenceEnding.Confirmed,
                CausalConsequenceHopKind.ScheduledDeletion => CausalConsequenceEnding.DeletionScheduled,
                CausalConsequenceHopKind.OutstandingPendingExport => GetPendingExportEnding(tuple.PendingExportStatus),
                _ => null
            }
        };
    }

    /// <summary>
    /// What an outstanding Pending Export is waiting for, by its live status.
    /// </summary>
    private static CausalConsequenceEnding GetPendingExportEnding(PendingExportStatus? status)
    {
        return status switch
        {
            PendingExportStatus.ExportNotConfirmed => CausalConsequenceEnding.WaitingForRetry,
            // A failing export is waiting for its next export run, which attempts it again (#2119); only a Parked one
            // waits on an administrator.
            PendingExportStatus.Parked => CausalConsequenceEnding.NeedsAttention,
            PendingExportStatus.Exported => CausalConsequenceEnding.WaitingForConfirmingImport,
            _ => CausalConsequenceEnding.WaitingForExport
        };
    }

    /// <summary>
    /// Whether an ending will move on without anyone acting. Needs attention is deliberately not waiting: nothing
    /// will happen until someone does something.
    /// </summary>
    private static bool IsWaiting(CausalConsequenceEnding ending)
    {
        return ending is CausalConsequenceEnding.WaitingForSynchronisation
            or CausalConsequenceEnding.WaitingForExport
            or CausalConsequenceEnding.WaitingForRetry
            or CausalConsequenceEnding.WaitingForConfirmingImport
            or CausalConsequenceEnding.DeletionScheduled;
    }

    private static bool HasWaitingBranch(CausalConsequenceCohort cohort)
    {
        return (cohort.Ending is { } ending && IsWaiting(ending))
            || cohort.Endings.Any(e => IsWaiting(e.Ending))
            || cohort.Consequences.Any(HasWaitingBranch);
    }

    /// <summary>
    /// Encodes a cohort's path from the root as an opaque, URL-safe key.
    /// </summary>
    private static string EncodeCohortKey(List<CausalConsequenceCohortTuple> path)
    {
        return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(path, CohortKeyJsonOptions));
    }

    /// <summary>
    /// Decodes a key written by <see cref="EncodeCohortKey"/>, or returns null for anything else: a key arrives from
    /// a request, so a malformed one is an expected input rather than a fault.
    /// </summary>
    private static List<CausalConsequenceCohortTuple>? TryDecodeCohortKey(string? cohortKey)
    {
        if (string.IsNullOrWhiteSpace(cohortKey) || !Base64Url.IsValid(cohortKey))
            return null;

        try
        {
            var path = JsonSerializer.Deserialize<List<CausalConsequenceCohortTuple>>(Base64Url.DecodeFromChars(cohortKey), CohortKeyJsonOptions);
            return path is { Count: > 0 and <= MaxCohortKeyDepth } ? path : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A cohort at the previous level (null for the root) and its path of shared values from the root.
    /// </summary>
    private sealed record ConsequenceParent(CausalConsequenceCohort? Cohort, List<CausalConsequenceCohortTuple> Path);
}
