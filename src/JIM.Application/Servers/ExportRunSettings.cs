// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Data.Repositories;
using JIM.Models.Core;

namespace JIM.Application.Servers;

/// <summary>
/// The settings an export run reads once rather than once per batch (#2895): which provisioning Synchronisation Rules ask
/// for an Initial Password, and the data type of the attributes external IDs are recorded in. Each is read the first time
/// a batch needs it, through that batch's own repository, and remembered for the rest of the run; a later batch reads
/// only what no earlier batch has. One instance per run, shared by its parallel batches.
/// </summary>
/// <remarks>
/// <para>
/// A run applies the configuration it read as it started, as a synchronisation applies the rules it loaded at its start
/// (<c>src/CLAUDE.md</c>, "A JimApplication Is One Unit of Work"); the Connected System and its settings are already read
/// once per export run. A change saved while the run is under way therefore takes effect at the next run rather than at
/// the run's next batch.
/// </para>
/// <para>
/// Only plain values are kept (ids, flags and data types), never an entity: each batch has a context of its own, and an
/// entity read on one must not travel to another. A read that fails is not remembered, so the next batch reads again. The
/// reads are serialised, so parallel batches starting together read once between them rather than once each.
/// </para>
/// </remarks>
internal sealed class ExportRunSettings : IDisposable
{
    private readonly ConcurrentDictionary<int, bool> _ruleAsksForAnInitialPassword = new();
    private readonly ConcurrentDictionary<int, AttributeDataType?> _attributeTypes = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);

    /// <summary>
    /// Which of the given Synchronisation Rules ask for an Initial Password.
    /// </summary>
    /// <param name="syncRuleIds">The batch's provisioning Synchronisation Rules.</param>
    /// <param name="repository">The batch's repository, for any rule the run has not read yet.</param>
    public async Task<HashSet<int>> GetRulesAskingForAnInitialPasswordAsync(IReadOnlyCollection<int> syncRuleIds, ISyncRepository repository)
    {
        await ReadMissingAsync(syncRuleIds, _ruleAsksForAnInitialPassword, async missing =>
        {
            var asking = await repository.GetSyncRuleIdsWithInitialPasswordEnabledAsync(missing);
            foreach (var syncRuleId in missing)
                _ruleAsksForAnInitialPassword[syncRuleId] = asking.Contains(syncRuleId);
        });

        return syncRuleIds.Where(syncRuleId => _ruleAsksForAnInitialPassword[syncRuleId]).ToHashSet();
    }

    /// <summary>
    /// The data type of each of the given attributes that exists; one that does not is left out.
    /// </summary>
    /// <param name="attributeIds">The attributes the batch records external IDs in.</param>
    /// <param name="repository">The batch's repository, for any attribute the run has not read yet.</param>
    public async Task<IReadOnlyDictionary<int, AttributeDataType>> GetAttributeTypesAsync(IReadOnlyCollection<int> attributeIds, ISyncRepository repository)
    {
        await ReadMissingAsync(attributeIds, _attributeTypes, async missing =>
        {
            var attributes = await repository.GetAttributesByIdsAsync(missing);
            foreach (var attributeId in missing)
                _attributeTypes[attributeId] = attributes.TryGetValue(attributeId, out var attribute) ? attribute.Type : null;
        });

        return attributeIds
            .Distinct()
            .Select(attributeId => (Id: attributeId, Type: _attributeTypes[attributeId]))
            .Where(entry => entry.Type.HasValue)
            .ToDictionary(entry => entry.Id, entry => entry.Type!.Value);
    }

    /// <summary>
    /// Reads the ids nothing has read yet, one read at a time across the run's batches, and checks again once the read
    /// gate is held, since the batch holding it before may have read them.
    /// </summary>
    private async Task ReadMissingAsync<TValue>(IReadOnlyCollection<int> ids, ConcurrentDictionary<int, TValue> known, Func<List<int>, Task> read)
    {
        if (ids.All(known.ContainsKey))
            return;

        await _readGate.WaitAsync();
        try
        {
            var missing = ids.Where(id => !known.ContainsKey(id)).Distinct().ToList();
            if (missing.Count > 0)
                await read(missing);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public void Dispose() => _readGate.Dispose();
}
