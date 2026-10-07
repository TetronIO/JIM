// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// The generated values held for an administrator's decision (Unique Value Generation, #242, release 4, Phase 9): a
/// target rejected a generated value as already in use and Collision Remediation did not correct it, because another
/// system has already accepted the value, because JIM cannot tell whether one has, or because the value has been
/// corrected as often as JIM allows. Serves the Generated Values tab on Operations, the needs-attention indicators on the
/// Synchronisation Rule and Connected System lists, the Metaverse Object banner, and their REST and PowerShell
/// counterparts, so the three surfaces cannot disagree about what is held or what an action does.
/// <para>
/// The lifecycle itself (entering a decision, and the "Allow the rename" and "Try again" exits) belongs to
/// <see cref="UniqueValueGenerationServer"/>, which knows nothing about Synchronisation Rules or who is asking. This
/// server adds what a person-facing surface needs on top: filters expressed in configuration terms (a Synchronisation
/// Rule, a Connected System), names on every row, and an audited Activity for every action recording who took it.
/// </para>
/// </summary>
public class GeneratedValueDecisionServer
{
    /// <summary>
    /// The window "corrected recently" counts over: the summary's "Corrected, last 7 days".
    /// </summary>
    public static readonly TimeSpan CorrectedWindow = TimeSpan.FromDays(7);

    private JimApplication Application { get; }

    internal GeneratedValueDecisionServer(JimApplication application)
    {
        Application = application;
    }

    // ---- Reads ----

    /// <summary>
    /// One window of the generated values held for a decision (and allowed renames waiting for the next export), newest
    /// first, narrowed by <paramref name="filter"/>: the Generated Values tab and <c>GET /generated-values/decisions</c>.
    /// </summary>
    /// <param name="filter">Which values; an empty filter means every held value.</param>
    /// <param name="startIndex">The zero-based offset of the window.</param>
    /// <param name="count">The window's size.</param>
    /// <param name="includeTotalCount">Whether to count every matching value as well (null total otherwise).</param>
    public async Task<RangeResultSet<GeneratedValueDecisionHeader>> GetDecisionsAsync(
        GeneratedValueDecisionFilter filter, int startIndex, int count, bool includeTotalCount)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var query = await ResolveAsync(filter, includeReleased: false);
        return await Application.SyncRepo.GetGeneratedValueDecisionHeadersAsync(query, startIndex, count, includeTotalCount);
    }

    /// <summary>
    /// One generated value by its assignment id, whatever its state: held, rename allowed, or released (what the single
    /// actions answer with, and <c>GET /generated-values/decisions/{id}</c>). Null when no generated value has that id.
    /// </summary>
    public async Task<GeneratedValueDecisionHeader?> GetDecisionAsync(Guid assignmentId)
    {
        var query = new GeneratedValueDecisionQuery { Ids = [assignmentId], IncludeReleased = true };
        var result = await Application.SyncRepo.GetGeneratedValueDecisionHeadersAsync(query, 0, 1, includeTotalCount: false);
        return result.Results.SingleOrDefault();
    }

    /// <summary>
    /// Every value held for a decision, or with its rename allowed, that belongs to one Metaverse Object: its own
    /// generated values, and export-mode values on the Connected System Objects joined to it. What the Metaverse Object's
    /// banner shows; empty when nothing is held.
    /// </summary>
    public async Task<List<GeneratedValueDecisionHeader>> GetDecisionsForMetaverseObjectAsync(Guid metaverseObjectId)
    {
        var query = new GeneratedValueDecisionQuery { MetaverseObjectId = metaverseObjectId };
        var result = await Application.SyncRepo.GetGeneratedValueDecisionHeadersAsync(query, 0, int.MaxValue, includeTotalCount: false);
        return result.Results;
    }

    /// <summary>
    /// The counts above the Generated Values tab, optionally narrowed to one Connected System or Synchronisation Rule
    /// (with the same meaning as <see cref="GeneratedValueDecisionFilter"/>): values needing a decision, allowed renames,
    /// and values Collision Remediation corrected in the last seven days.
    /// <para>
    /// "Corrected" is read from each value's <see cref="GeneratedValueAssignment.RemediatedAt"/>: a value counts once,
    /// from its most recent correction, and only while it still exists. Activity statistics were the alternative, and
    /// were rejected: they count correction events (one value corrected twice counts twice), cannot be narrowed to a
    /// Synchronisation Rule, and would mean aggregating execution item outcomes over a week of Activities on every read.
    /// </para>
    /// </summary>
    public async Task<GeneratedValueDecisionSummary> GetSummaryAsync(int? connectedSystemId = null, int? syncRuleId = null)
    {
        var correctedSince = DateTime.UtcNow - CorrectedWindow;
        var counts = await Application.SyncRepo.GetGeneratedValueDecisionCountsAsync(correctedSince);
        var configuration = counts.Count == 0 ? GeneratedFlows.Empty : await LoadGeneratedFlowsAsync();

        var matching = counts.Where(c =>
            (!syncRuleId.HasValue || configuration.SyncRuleIdOf(c.GenerationId) == syncRuleId.Value) &&
            (!connectedSystemId.HasValue || configuration.SystemsInvolved(c).Contains(connectedSystemId.Value))).ToList();

        return new GeneratedValueDecisionSummary
        {
            NeedsDecisionCount = matching.Sum(c => c.NeedsDecisionCount),
            RenameAllowedCount = matching.Sum(c => c.RenameAllowedCount),
            CorrectedRecentlyCount = matching.Sum(c => c.CorrectedCount),
            CorrectedSince = correctedSince
        };
    }

    /// <summary>
    /// How many values generated by each of these Synchronisation Rules' Attribute Flows are held, for the indicator on the
    /// Synchronisation Rules list. A rule with nothing held is absent rather than present with zeroes.
    /// </summary>
    public async Task<Dictionary<int, GeneratedValueDecisionAttention>> GetAttentionBySyncRuleAsync(IReadOnlyCollection<int> syncRuleIds)
    {
        ArgumentNullException.ThrowIfNull(syncRuleIds);

        var (counts, configuration) = await LoadHeldCountsAsync();
        var wanted = syncRuleIds.ToHashSet();
        var attention = new Dictionary<int, GeneratedValueDecisionAttention>();

        foreach (var (syncRuleId, count) in counts
                     .Select(c => (SyncRuleId: configuration.SyncRuleIdOf(c.GenerationId), Count: c))
                     .Where(x => x.SyncRuleId.HasValue && wanted.Contains(x.SyncRuleId.Value)))
            Add(attention, syncRuleId!.Value, count);

        return attention;
    }

    /// <summary>
    /// How many held values involve each of these Connected Systems (it rejected the value, anchors it, or the value is
    /// exported to and checked in it), for the indicator on the Connected Systems list. A value involving a system in more
    /// than one way counts once for it. A system with nothing held is absent rather than present with zeroes.
    /// </summary>
    public async Task<Dictionary<int, GeneratedValueDecisionAttention>> GetAttentionByConnectedSystemAsync(IReadOnlyCollection<int> connectedSystemIds)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemIds);

        var (counts, configuration) = await LoadHeldCountsAsync();
        var wanted = connectedSystemIds.ToHashSet();
        var attention = new Dictionary<int, GeneratedValueDecisionAttention>();

        foreach (var count in counts)
        {
            foreach (var connectedSystemId in configuration.SystemsInvolved(count).Where(wanted.Contains))
                Add(attention, connectedSystemId, count);
        }

        return attention;
    }

    /// <summary>
    /// What allowing the rename of one held value would do, for the "Allow the rename" confirmation (plan decision 11): every
    /// Connected System the value is exported to where the object has an account, and whether that account is renamed,
    /// created or updated; and the value JIM is likely to choose, where that is cheap to say. The value itself is chosen by
    /// the worker at the next export, after checking every system again. Null when no generated value has that id.
    /// </summary>
    public async Task<GeneratedValueRenamePreview?> GetRenamePreviewAsync(Guid assignmentId)
    {
        var decision = await GetDecisionAsync(assignmentId);
        if (decision == null)
            return null;

        var mapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(decision.SyncRuleMappingId);
        var participants = mapping?.Generation == null
            ? []
            : await Application.ConnectedSystems.GetGeneratedValueParticipantsAsync(mapping, mapping.SyncRule?.ConnectedSystemId ?? 0);

        List<ConnectedSystemObject> accounts;
        if (decision.MetaverseObjectId.HasValue)
        {
            accounts = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(decision.MetaverseObjectId.Value);
        }
        else
        {
            var account = decision is { ConnectedSystemObjectId: { } csoId, ConnectedSystemObjectConnectedSystemId: { } systemId }
                ? await Application.ConnectedSystems.GetConnectedSystemObjectAsync(systemId, csoId)
                : null;
            accounts = account == null ? [] : [account];
        }

        var assignment = await Application.SyncRepo.GetGeneratedValueAssignmentByIdAsync(assignmentId);
        return new GeneratedValueRenamePreview
        {
            Decision = decision,
            Changes = GeneratedValueRenamePlanner.Plan(participants, accounts, decision.Value),
            LikelyValue = mapping?.Generation == null || assignment == null
                ? null
                : GeneratedValueRenamePlanner.LikelyNextValue(mapping.Generation, assignment.BaseValue ?? FallbackBase(assignment), decision.Value)
        };
    }

    /// <summary>
    /// An assignment made before its base was recorded: its own value is the base when nothing was added to it, which is
    /// the common case and the same fallback Collision Remediation uses.
    /// </summary>
    private static string FallbackBase(GeneratedValueAssignment assignment) => assignment.Value;

    // ---- Actions ----

    /// <summary>
    /// "Allow the rename" (plan decision 11): authorises JIM to choose the next free value at the next export that meets
    /// the rejection and apply it everywhere, renaming the account that already holds the current one; releases the held
    /// export so that export happens; and records an Activity naming who allowed it. Exactly one of
    /// <paramref name="initiatedBy"/> and <paramref name="initiatedByApiKey"/> is expected; the API key wins if both are given.
    /// </summary>
    /// <exception cref="ArgumentException">Neither initiator was given: every decision is attributed.</exception>
    public Task<GeneratedValueDecisionActionOutcome> AllowRenameAsync(Guid assignmentId, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey) =>
        ActAsync(assignmentId, initiatedBy, initiatedByApiKey, ActivityTargetOperationType.AllowGeneratedValueRename);

    /// <summary>
    /// "Try again" (plan decision 11): releases the held export so the next export run tries the same value, for when the
    /// clash has been resolved in the target; and records an Activity naming who released it. If the target rejects the
    /// value again, it comes back for a decision. An allowed rename is not waiting for a decision, so it is left alone.
    /// </summary>
    /// <exception cref="ArgumentException">Neither initiator was given.</exception>
    public Task<GeneratedValueDecisionActionOutcome> TryAgainAsync(Guid assignmentId, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey) =>
        ActAsync(assignmentId, initiatedBy, initiatedByApiKey, ActivityTargetOperationType.RetryGeneratedValue);

    /// <summary>
    /// "Try again" for every value matching <paramref name="filter"/> that needs a decision (its status is ignored: an
    /// allowed rename is never released by this), each recorded by its own Activity so every object's history says who
    /// released its value. Returns how many were released.
    /// </summary>
    /// <exception cref="ArgumentException">Neither initiator was given.</exception>
    public async Task<int> TryAgainAsync(GeneratedValueDecisionFilter filter, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey)
    {
        ArgumentNullException.ThrowIfNull(filter);
        RequireInitiator(initiatedBy, initiatedByApiKey);

        var query = await ResolveAsync(filter, includeReleased: false);
        query.Status = GeneratedValueDecisionStatus.NeedsDecision;
        var held = (await Application.SyncRepo.GetGeneratedValueDecisionHeadersAsync(query, 0, int.MaxValue, includeTotalCount: false)).Results;

        var released = 0;
        foreach (var row in held)
        {
            // False when something else released it between the read and now; nothing to record then.
            var releasedNow = await Application.UniqueValues.RetryAsync(row.AssignmentId);
            if (!releasedNow)
                continue;

            await RecordAsync(row, ActivityTargetOperationType.RetryGeneratedValue, initiatedBy, initiatedByApiKey);
            released++;
        }

        Log.Information("TryAgainAsync: {Released} of {Matched} generated value(s) needing a decision were released for the next export to try again.",
            released, held.Count);
        return released;
    }

    private async Task<GeneratedValueDecisionActionOutcome> ActAsync(
        Guid assignmentId, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey, ActivityTargetOperationType operation)
    {
        RequireInitiator(initiatedBy, initiatedByApiKey);

        var row = await GetDecisionAsync(assignmentId);
        if (row == null)
            return GeneratedValueDecisionActionOutcome.NotFound;

        if (row.Status != GeneratedValueDecisionStatus.NeedsDecision)
            return GeneratedValueDecisionActionOutcome.NotWaitingForDecision;

        var done = operation == ActivityTargetOperationType.AllowGeneratedValueRename
            ? await Application.UniqueValues.AuthoriseRenameAsync(assignmentId, InitiatorName(initiatedBy, initiatedByApiKey))
            : await Application.UniqueValues.RetryAsync(assignmentId);

        // Something else released it between the read and the write (a configuration save, another administrator).
        if (!done)
            return GeneratedValueDecisionActionOutcome.NotWaitingForDecision;

        await RecordAsync(row, operation, initiatedBy, initiatedByApiKey);
        Log.Information("{Operation}: generated value {AssignmentId} ({Attribute}) was released for the next export run, by {InitiatorType}.",
            operation, assignmentId, LogSanitiser.Sanitise(row.AttributeName), initiatedByApiKey != null ? "API key" : "user");
        return GeneratedValueDecisionActionOutcome.Done;
    }

    /// <summary>
    /// The audit record of one decision: against the Metaverse Object for its own value, or against the account for an
    /// export-mode value, naming the Synchronisation Rule whose flow generated it.
    /// </summary>
    private async Task RecordAsync(GeneratedValueDecisionHeader row, ActivityTargetOperationType operation, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey)
    {
        var exportMode = row.ConnectedSystemObjectId.HasValue;
        var activity = new Activity
        {
            TargetType = exportMode ? ActivityTargetType.ConnectedSystemObject : ActivityTargetType.MetaverseObject,
            TargetOperationType = operation,
            TargetName = row.MetaverseObjectDisplayName ?? row.Value,
            TargetContext = row.AttributeName,
            MetaverseObjectId = row.MetaverseObjectId,
            ConnectedSystemObjectId = row.ConnectedSystemObjectId,
            ConnectedSystemId = row.ConnectedSystemObjectConnectedSystemId,
            SyncRuleId = row.SyncRuleId,
            Message = Describe(row, operation)
        };

        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    private static string Describe(GeneratedValueDecisionHeader row, ActivityTargetOperationType operation)
    {
        var rejectedBy = row.RejectedByConnectedSystemName ?? (row.RejectedByConnectedSystemId.HasValue ? $"Connected System {row.RejectedByConnectedSystemId}" : "a target");
        if (operation != ActivityTargetOperationType.AllowGeneratedValueRename)
            return $"Released the {row.AttributeName} \"{row.Value}\", which {rejectedBy} rejected as already in use, so the next export tries the same value again.";

        var renames = row.AnchoredByConnectedSystemName != null && row.Reason == GeneratedValueNeedsDecisionReason.AnchoredElsewhere
            ? $", renaming the account in {row.AnchoredByConnectedSystemName}"
            : string.Empty;
        return $"Allowed the rename of the {row.AttributeName} \"{row.Value}\", which {rejectedBy} rejected as already in use: at the next export, " +
               $"JIM chooses the next free value and applies it everywhere{renames}.";
    }

    /// <summary>
    /// The name an allowed rename records: the API key's name, or the user's display name (their id when they have none),
    /// retained even if the principal is later deleted.
    /// </summary>
    private static string InitiatorName(MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey) =>
        initiatedByApiKey?.Name ?? initiatedBy!.Name ?? initiatedBy.Id.ToString();

    private static void RequireInitiator(MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey)
    {
        if (initiatedBy == null && initiatedByApiKey == null)
            throw new ArgumentException("A decision about a generated value must be attributed to the user or API key that made it.");
    }

    // ---- Configuration ----

    private async Task<GeneratedValueDecisionQuery> ResolveAsync(GeneratedValueDecisionFilter filter, bool includeReleased)
    {
        var query = new GeneratedValueDecisionQuery
        {
            Status = filter.Status,
            IncludeReleased = includeReleased,
            MetaverseObjectId = filter.MetaverseObjectId,
            Ids = filter.Ids is { Count: > 0 } ? filter.Ids : null
        };

        if (!filter.SyncRuleId.HasValue && !filter.ConnectedSystemId.HasValue)
            return query;

        var configuration = await LoadGeneratedFlowsAsync();
        if (filter.SyncRuleId.HasValue)
            query.GenerationIds = configuration.GenerationIdsOf(filter.SyncRuleId.Value);

        if (filter.ConnectedSystemId.HasValue)
        {
            query.ConnectedSystemId = filter.ConnectedSystemId.Value;
            query.GenerationIdsParticipatingInConnectedSystem = configuration.GenerationIdsParticipatingIn(filter.ConnectedSystemId.Value);
        }

        return query;
    }

    private async Task<(List<GeneratedValueDecisionCount> Counts, GeneratedFlows Configuration)> LoadHeldCountsAsync()
    {
        var counts = (await Application.SyncRepo.GetGeneratedValueDecisionCountsAsync(DateTime.UtcNow - CorrectedWindow))
            .Where(c => c.NeedsDecisionCount > 0 || c.RenameAllowedCount > 0)
            .ToList();
        return (counts, counts.Count == 0 ? GeneratedFlows.Empty : await LoadGeneratedFlowsAsync());
    }

    private static void Add(Dictionary<int, GeneratedValueDecisionAttention> attention, int key, GeneratedValueDecisionCount count)
    {
        if (!attention.TryGetValue(key, out var entry))
            attention[key] = entry = new GeneratedValueDecisionAttention();

        entry.NeedsDecisionCount += count.NeedsDecisionCount;
        entry.RenameAllowedCount += count.RenameAllowedCount;
    }

    /// <summary>
    /// Every generated flow JIM holds: the Synchronisation Rule it is on, and the Connected Systems its value is exported to
    /// and checked in (an import-mode flow's participating targets, computed exactly as the engine computes them; an
    /// export-mode flow's own system).
    /// </summary>
    private async Task<GeneratedFlows> LoadGeneratedFlowsAsync()
    {
        var rules = await Application.SyncRepo.GetAllSyncRulesAsync();
        var exportRules = rules.Where(r => r.Direction == SyncRuleDirection.Export).ToList();

        var flows = rules
            .SelectMany(rule => rule.AttributeFlowRules.Where(m => m.Generation != null).Select(mapping => (Rule: rule, Mapping: mapping)))
            .GroupBy(x => x.Mapping.Generation!.Id)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var (rule, mapping) = g.First();
                    var systems = mapping.TargetMetaverseAttributeId.HasValue
                        ? GeneratedValueParticipation.ComputeParticipatingTargets(mapping, exportRules).Select(t => t.ConnectedSystemId).ToHashSet()
                        : [rule.ConnectedSystemId];
                    return new GeneratedFlow(rule.Id, systems);
                });

        return new GeneratedFlows(flows);
    }

    private sealed record GeneratedFlow(int SyncRuleId, HashSet<int> ParticipatingSystemIds);

    private sealed class GeneratedFlows(Dictionary<int, GeneratedFlow> flows)
    {
        public static GeneratedFlows Empty { get; } = new([]);

        public int? SyncRuleIdOf(int generationId) => flows.TryGetValue(generationId, out var flow) ? flow.SyncRuleId : null;

        public List<int> GenerationIdsOf(int syncRuleId) => flows.Where(f => f.Value.SyncRuleId == syncRuleId).Select(f => f.Key).ToList();

        public List<int> GenerationIdsParticipatingIn(int connectedSystemId) =>
            flows.Where(f => f.Value.ParticipatingSystemIds.Contains(connectedSystemId)).Select(f => f.Key).ToList();

        public HashSet<int> SystemsInvolved(GeneratedValueDecisionCount count)
        {
            var systems = flows.TryGetValue(count.GenerationId, out var flow) ? new HashSet<int>(flow.ParticipatingSystemIds) : [];
            if (count.RejectedByConnectedSystemId.HasValue)
                systems.Add(count.RejectedByConnectedSystemId.Value);
            if (count.AnchoredByConnectedSystemId.HasValue)
                systems.Add(count.AnchoredByConnectedSystemId.Value);
            return systems;
        }
    }
}
