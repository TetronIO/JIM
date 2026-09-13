// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// Resolves a contributing Synchronisation Rule's name from its id for
/// <see cref="JIM.Models.Core.MetaverseObjectChange.AddAttributeValueChange"/> (#1519 follow-up), backed by an
/// in-memory cache so a batch that touches the same rule (or the same unresolvable id) many times costs at most
/// one repository round trip in total, not one per attribute value.
/// <para>
/// Seed the cache with whatever Synchronisation Rules the caller already holds in memory (the active rules a
/// sync run loaded for its own purposes, say) so the common case never reaches the database at all. Call
/// <see cref="WarmAsync"/> with every distinct id the caller is about to resolve before reading
/// <see cref="Resolve"/>: <see cref="JIM.Models.Core.MetaverseObjectChange.AddAttributeValueChange"/> needs a synchronous
/// <c>Func&lt;int, string?&gt;</c>, so the async repository read must happen up front rather than lazily inside
/// the resolver delegate.
/// </para>
/// <para>
/// A surviving contributor from a Connected System other than the one currently being synced (after a recall or
/// re-election, #1533/#1537) will not appear in a caller's own "active rules for this system" seed; that is what
/// <see cref="WarmAsync"/>'s repository fallback covers. A genuinely deleted rule resolves to null and is cached
/// as such, so a repeated miss never re-queries.
/// </para>
/// </summary>
public sealed class SyncRuleNameResolverCache
{
    private readonly ISyncRepository _syncRepo;
    private readonly Dictionary<int, string?> _namesById = new();

    /// <param name="syncRepo">Used only when <see cref="WarmAsync"/> is asked to resolve an id not already
    /// known from <paramref name="knownRules"/> or an earlier warm.</param>
    /// <param name="knownRules">Synchronisation Rules the caller already holds in memory; seeds the cache so
    /// resolving one of them never reaches the database.</param>
    public SyncRuleNameResolverCache(ISyncRepository syncRepo, IEnumerable<SyncRule>? knownRules = null)
    {
        _syncRepo = syncRepo;

        if (knownRules == null)
            return;

        foreach (var rule in knownRules)
            _namesById[rule.Id] = rule.Name;
    }

    /// <summary>
    /// Resolves every id not already known (from the constructor's seed or an earlier call to this method) with
    /// a single batched repository call. An id the repository does not return for (the rule has been deleted)
    /// is cached as null, so it is never queried again.
    /// </summary>
    public async Task WarmAsync(IEnumerable<int> syncRuleIds)
    {
        var unresolved = syncRuleIds.Distinct().Where(id => !_namesById.ContainsKey(id)).ToList();
        if (unresolved.Count == 0)
            return;

        var names = await _syncRepo.GetSyncRuleNamesByIdsAsync(unresolved);
        foreach (var id in unresolved)
            _namesById[id] = names.GetValueOrDefault(id);
    }

    /// <summary>
    /// Resolves a Synchronisation Rule id to its name from the cache. Returns null for an id that was never
    /// seeded and never passed to <see cref="WarmAsync"/>, or that <see cref="WarmAsync"/> found to be deleted.
    /// Never itself touches the database; suitable for passing as
    /// <see cref="JIM.Models.Core.MetaverseObjectChange.AddAttributeValueChange"/>'s resolver delegate via
    /// <see cref="Resolve"/>'s method group.
    /// </summary>
    public string? Resolve(int syncRuleId) => _namesById.GetValueOrDefault(syncRuleId);
}
