// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Generated value decisions (#242, release 4, Phase 9)

    /// <inheritdoc />
    public async Task<RangeResultSet<GeneratedValueDecisionHeader>> GetGeneratedValueDecisionHeadersAsync(
        GeneratedValueDecisionQuery query, int offset, int count, bool includeTotalCount)
    {
        ArgumentNullException.ThrowIfNull(query);

        var matching = ApplyDecisionQuery(_context.GeneratedValueAssignments.AsNoTracking(), query);
        int? totalCount = includeTotalCount ? await matching.CountAsync() : null;

        var window = OrderDecisions(matching).Skip(offset).Take(count);
        var rows = await ProjectDecisionHeaders(window).ToListAsync();

        // The projection's own ORDER BY is not guaranteed to survive the joins it adds, so restore the window's order.
        return new RangeResultSet<GeneratedValueDecisionHeader>
        {
            Results = rows.OrderBy(r => r.Since == null).ThenByDescending(r => r.Since).ThenBy(r => r.AssignmentId).ToList(),
            TotalResults = totalCount
        };
    }

    /// <inheritdoc />
    public Task<List<Guid>> GetGeneratedValueDecisionIdsAsync(GeneratedValueDecisionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return OrderDecisions(ApplyDecisionQuery(_context.GeneratedValueAssignments.AsNoTracking(), query))
            .Select(a => a.Id)
            .ToListAsync();
    }

    /// <inheritdoc />
    public Task<List<GeneratedValueDecisionCount>> GetGeneratedValueDecisionCountsAsync(DateTime correctedSince)
    {
        // The three arms of the filter are each served by an index (the State index; the partial indexes on
        // RenameAuthorised and RemediatedAt), so this reads only the handful of rows that matter however many generated
        // values the estate holds.
        return _context.GeneratedValueAssignments.AsNoTracking()
            .Where(a => a.State == GeneratedValueAssignmentState.NeedsDecision || a.RenameAuthorised || a.RemediatedAt >= correctedSince)
            .GroupBy(a => new { a.SyncRuleMappingGenerationId, a.RejectedByConnectedSystemId, a.AnchoredByConnectedSystemId })
            .Select(g => new GeneratedValueDecisionCount
            {
                GenerationId = g.Key.SyncRuleMappingGenerationId,
                RejectedByConnectedSystemId = g.Key.RejectedByConnectedSystemId,
                AnchoredByConnectedSystemId = g.Key.AnchoredByConnectedSystemId,
                NeedsDecisionCount = g.Count(a => a.State == GeneratedValueAssignmentState.NeedsDecision),
                RenameAllowedCount = g.Count(a => a.State != GeneratedValueAssignmentState.NeedsDecision && a.RenameAuthorised),
                CorrectedCount = g.Count(a => a.RemediatedAt >= correctedSince)
            })
            .ToListAsync();
    }

    /// <summary>
    /// Narrows assignments to what <paramref name="query"/> asks for. Status is derived exactly as the header derives it:
    /// Needs Decision is the state; an allowed rename is an authorisation not yet spent on a value no longer held.
    /// </summary>
    private static IQueryable<GeneratedValueAssignment> ApplyDecisionQuery(IQueryable<GeneratedValueAssignment> source, GeneratedValueDecisionQuery query)
    {
        const GeneratedValueAssignmentState needsDecision = GeneratedValueAssignmentState.NeedsDecision;

        source = query.Status switch
        {
            GeneratedValueDecisionStatus.NeedsDecision => source.Where(a => a.State == needsDecision),
            GeneratedValueDecisionStatus.RenameAllowed => source.Where(a => a.State != needsDecision && a.RenameAuthorised),
            GeneratedValueDecisionStatus.Released => source.Where(a => a.State != needsDecision && !a.RenameAuthorised),
            _ when query.IncludeReleased => source,
            _ => source.Where(a => a.State == needsDecision || a.RenameAuthorised)
        };

        if (query.GenerationIds != null)
        {
            var generationIds = query.GenerationIds.ToList();
            source = source.Where(a => generationIds.Contains(a.SyncRuleMappingGenerationId));
        }

        if (query.ConnectedSystemId.HasValue)
        {
            var connectedSystemId = query.ConnectedSystemId.Value;
            var participating = query.GenerationIdsParticipatingInConnectedSystem.ToList();
            source = source.Where(a => a.RejectedByConnectedSystemId == connectedSystemId
                                       || a.AnchoredByConnectedSystemId == connectedSystemId
                                       || participating.Contains(a.SyncRuleMappingGenerationId));
        }

        if (query.MetaverseObjectId.HasValue)
        {
            var metaverseObjectId = query.MetaverseObjectId.Value;
            source = source.Where(a => a.MetaverseObjectId == metaverseObjectId
                                       || (a.ConnectedSystemObjectId != null && a.ConnectedSystemObject!.MetaverseObjectId == metaverseObjectId));
        }

        if (query.Ids != null)
        {
            var ids = query.Ids.ToList();
            source = source.Where(a => ids.Contains(a.Id));
        }

        return source;
    }

    /// <summary>
    /// Newest decision first, by when the value began waiting; values with no such time (a released value read by id)
    /// last; the id breaks ties so windows never overlap.
    /// </summary>
    private static IQueryable<GeneratedValueAssignment> OrderDecisions(IQueryable<GeneratedValueAssignment> source) =>
        source.OrderBy(a => a.NeedsDecisionEnteredAt == null)
            .ThenByDescending(a => a.NeedsDecisionEnteredAt)
            .ThenBy(a => a.Id);

    /// <summary>
    /// The header projection: the assignment, plus what is known now about its object, attribute and flow, and the
    /// systems it names. Both system ids are plain columns (they outlive a deleted system), so they are left joins.
    /// </summary>
    private IQueryable<GeneratedValueDecisionHeader> ProjectDecisionHeaders(IQueryable<GeneratedValueAssignment> source)
    {
        return from a in source
               join rejected in _context.ConnectedSystems on a.RejectedByConnectedSystemId equals (int?)rejected.Id into rejectedSystems
               from rejected in rejectedSystems.DefaultIfEmpty()
               join anchored in _context.ConnectedSystems on a.AnchoredByConnectedSystemId equals (int?)anchored.Id into anchoredSystems
               from anchored in anchoredSystems.DefaultIfEmpty()
               select new GeneratedValueDecisionHeader
               {
                   AssignmentId = a.Id,
                   Status = a.State == GeneratedValueAssignmentState.NeedsDecision
                       ? GeneratedValueDecisionStatus.NeedsDecision
                       : a.RenameAuthorised ? GeneratedValueDecisionStatus.RenameAllowed : GeneratedValueDecisionStatus.Released,
                   // Import mode: the value's own object. Export mode: the object its account is joined to, if any.
                   MetaverseObjectId = a.MetaverseObjectId != null ? a.MetaverseObjectId : a.ConnectedSystemObject!.MetaverseObjectId,
                   MetaverseObjectDisplayName = a.MetaverseObjectId != null
                       ? a.MetaverseObject!.CachedDisplayName
                       : a.ConnectedSystemObject!.MetaverseObject!.CachedDisplayName,
                   MetaverseObjectTypeName = a.MetaverseObjectId != null
                       ? a.MetaverseObject!.Type.Name
                       : a.ConnectedSystemObject!.MetaverseObject!.Type.Name,
                   MetaverseObjectTypePluralName = a.MetaverseObjectId != null
                       ? a.MetaverseObject!.Type.PluralName
                       : a.ConnectedSystemObject!.MetaverseObject!.Type.PluralName,
                   ConnectedSystemObjectId = a.ConnectedSystemObjectId,
                   ConnectedSystemObjectConnectedSystemId = a.ConnectedSystemObjectId != null ? (int?)a.ConnectedSystemObject!.ConnectedSystemId : null,
                   AttributeName = a.MetaverseAttributeId != null ? a.MetaverseAttribute!.Name : a.ConnectedSystemObjectTypeAttribute!.Name,
                   Value = a.Value,
                   Reason = a.NeedsDecisionReason,
                   RemediationCount = a.RemediationCount,
                   RejectedByConnectedSystemId = a.RejectedByConnectedSystemId,
                   RejectedByConnectedSystemName = rejected != null ? rejected.Name : null,
                   AnchoredByConnectedSystemId = a.AnchoredByConnectedSystemId,
                   AnchoredByConnectedSystemName = anchored != null ? anchored.Name : null,
                   Since = a.NeedsDecisionEnteredAt,
                   RenameAllowedAt = a.State != GeneratedValueAssignmentState.NeedsDecision && a.RenameAuthorised ? a.RenameAuthorisedAt : null,
                   RenameAllowedBy = a.State != GeneratedValueAssignmentState.NeedsDecision && a.RenameAuthorised ? a.RenameAuthorisedByName : null,
                   SyncRuleId = a.SyncRuleMappingGeneration!.SyncRuleMapping!.SyncRuleId,
                   SyncRuleName = a.SyncRuleMappingGeneration!.SyncRuleMapping!.SyncRule!.Name,
                   SyncRuleMappingId = a.SyncRuleMappingGeneration!.SyncRuleMappingId
               };
    }

    #endregion
}
