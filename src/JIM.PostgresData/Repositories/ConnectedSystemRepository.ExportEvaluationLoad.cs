// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

public partial class ConnectedSystemRepository
{
    /// <summary>
    /// The target Connected System Objects' attribute values for export evaluation's no-net-change detection, with each
    /// attribute a member question names loaded in part (#2832).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A membership change stages only the members that changed, so no-net-change detection asks only whether the target
    /// holds those. Loading a large group whole to answer it read every member: hundreds of thousands of rows for a group
    /// that gained or lost one. A member change staged before export resolution names the member's Metaverse Object, and a
    /// held value can match it only by naming an object joined to that Metaverse Object, or, unresolved, by its text
    /// (<c>SyncEngine.ReferenceValueMatches</c>). So each question loads the values naming the objects joined to its
    /// Metaverse Objects, by the <c>ReferenceValueId</c> index, and the unresolved values whose text is one of their ids,
    /// lowered (a GUID is ASCII hexadecimal, which every collation lowers alike): reconciliation's probe asks exactly these
    /// (<see cref="SyncRepository.GetReconciliationProbeValuesAsync"/>). The engine's own comparison then decides each answer
    /// over a superset of the values that could match.
    /// </para>
    /// <para>
    /// Every other value is loaded whole, by ranges of attribute id that leave the questioned attributes out, so no
    /// statement reads a member row it does not return (<see cref="AttributeValuesByRangesSql"/>). The values are loaded
    /// exactly as <see cref="GetCsoAttributeValuesByCsoIdsAsync"/> loads them: with their object, attribute and referenced
    /// object, and tracked where the context tracks, so the objects already loaded for the page see them as before.
    /// </para>
    /// </remarks>
    public async Task<List<ConnectedSystemObjectAttributeValue>> GetCsoAttributeValuesForExportEvaluationAsync(
        IReadOnlyCollection<Guid> csoIds, IReadOnlyCollection<ExportEvaluationMemberQuestion> memberQuestions)
    {
        if (csoIds.Count == 0)
            return [];

        // The EF test doubles run no SQL; they are handed every value, a superset of what any question loads.
        if (memberQuestions.Count == 0 || Repository.Database.Database?.IsRelational() != true)
            return await GetCsoAttributeValuesByCsoIdsAsync(csoIds);

        var duplicate = memberQuestions.GroupBy(q => (q.ConnectedSystemObjectId, q.AttributeId)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException(
                $"More than one member question for attribute {duplicate.Key.AttributeId} of Connected System Object {duplicate.Key.ConnectedSystemObjectId}.",
                nameof(memberQuestions));

        var candidateValueIds = await GetMemberCandidateValueIdsAsync(memberQuestions);

        var excludedAttributeIdsByObject = memberQuestions
            .GroupBy(q => q.ConnectedSystemObjectId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<int>)g.Select(q => q.AttributeId).ToList());
        var objects = csoIds
            .Distinct()
            .Select(id => (id, excludedAttributeIdsByObject.TryGetValue(id, out var excluded) ? excluded : (IReadOnlyCollection<int>)[]))
            .ToList();

        var values = await Repository.Database.ConnectedSystemObjectAttributeValues
            .FromSqlRaw(AttributeValuesByRangesSql, AttributeValuesByRangesParameters(objects, candidateValueIds).Cast<object>().ToArray())
            .Include(av => av.ConnectedSystemObject)
            .Include(av => av.Attribute)
            .Include(av => av.ReferenceValue)
            .ToListAsync();

        return values.DistinctBy(av => av.Id).ToList();
    }

    /// <summary>
    /// The ids of the values the member questions could match: those naming an object joined to one of a question's
    /// Metaverse Objects, and those unresolved whose text is one of their ids.
    /// </summary>
    private async Task<HashSet<Guid>> GetMemberCandidateValueIdsAsync(IReadOnlyCollection<ExportEvaluationMemberQuestion> memberQuestions)
    {
        var metaverseObjectIds = memberQuestions.SelectMany(q => q.MetaverseObjectIds).Distinct().ToList();
        if (metaverseObjectIds.Count == 0)
            return [];

        var joinedObjects = await Repository.Database.ConnectedSystemObjects
            .AsNoTracking()
            .Where(cso => cso.MetaverseObjectId.HasValue && metaverseObjectIds.Contains(cso.MetaverseObjectId.Value))
            .Select(cso => new { cso.Id, MetaverseObjectId = cso.MetaverseObjectId!.Value })
            .ToListAsync();
        var objectIdsByMetaverseObjectId = joinedObjects.ToLookup(o => o.MetaverseObjectId, o => o.Id);

        var probeQuestions = memberQuestions
            .Where(q => q.MetaverseObjectIds.Count > 0)
            .Select(q => new ReconciliationProbeQuestion
            {
                ConnectedSystemObjectId = q.ConnectedSystemObjectId,
                AttributeId = q.AttributeId,
                ChangeIds = new HashSet<Guid>(),
                ReferenceIds = q.MetaverseObjectIds.SelectMany(id => objectIdsByMetaverseObjectId[id]).Distinct().ToList(),
                ExactValues = [],
                CaseInsensitiveValues = q.MetaverseObjectIds.Select(id => id.ToString()).ToList(),
                NeedsCount = false
            })
            .ToList();

        var answers = await Repository.Sync.GetReconciliationProbeValuesAsync(probeQuestions);
        return answers.SelectMany(answer => answer.CandidateValues).Select(value => value.Id).ToHashSet();
    }
}
