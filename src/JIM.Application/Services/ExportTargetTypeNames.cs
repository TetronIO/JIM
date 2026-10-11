// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Transactional;

namespace JIM.Application.Services;

/// <summary>
/// Names the Connected System Object Type an export outcome's target belongs to, for the "csId|csoTypeName" channel
/// the causality panel reads to name the object ("user: Alice Smith"). Built from the export Synchronisation Rules and
/// asked by the most exact key the caller holds: the object's own type, the rule that acted on it, or the Metaverse
/// Object Type it was exported for.
/// </summary>
/// <remarks>
/// Deliberately never by the Connected System alone. A directory that takes both users and groups has an export
/// rule for each, so "the type this system exports" has no single answer; the per-system lookup this replaced took
/// whichever rule came first, and recorded every user provisioned to such a directory as a group. Where the key a
/// caller holds is ambiguous too, the answer is no name rather than a guess: the panel reads a missing type as "name
/// the object alone", which is honest, where a wrong one is a false record in the Activity's history.
/// </remarks>
public sealed class ExportTargetTypeNames
{
    private readonly Dictionary<int, string> _namesByObjectTypeId = [];
    private readonly Dictionary<int, string> _namesBySyncRuleId = [];
    private readonly Dictionary<(int ConnectedSystemId, int MetaverseObjectTypeId), string?> _namesByMetaverseObjectType = [];
    private readonly Dictionary<int, string?> _namesByConnectedSystemId = [];

    private ExportTargetTypeNames()
    {
    }

    /// <summary>
    /// Names nothing: for a run with no export evaluation cache.
    /// </summary>
    public static ExportTargetTypeNames Empty { get; } = new();

    /// <summary>
    /// Built from an export evaluation cache's rules; <see cref="Empty"/> where there is no cache.
    /// </summary>
    public static ExportTargetTypeNames From(ExportEvaluationCache? cache) =>
        From(cache?.ExportRulesByMvoTypeId.Values.SelectMany(rules => rules));

    /// <summary>
    /// Built from export Synchronisation Rules. A rule whose object type is not loaded names nothing.
    /// </summary>
    public static ExportTargetTypeNames From(IEnumerable<SyncRule>? exportRules)
    {
        if (exportRules == null)
            return Empty;

        var names = new ExportTargetTypeNames();
        foreach (var rule in exportRules.Where(r => r.ConnectedSystemObjectType != null))
        {
            var typeName = rule.ConnectedSystemObjectType.Name;
            names._namesByObjectTypeId[rule.ConnectedSystemObjectType.Id] = typeName;
            names._namesBySyncRuleId[rule.Id] = typeName;

            // A second, different type for the same key makes the key ambiguous: null, for good.
            AddUnlessAmbiguous(names._namesByMetaverseObjectType, (rule.ConnectedSystemId, rule.MetaverseObjectTypeId), typeName);
            AddUnlessAmbiguous(names._namesByConnectedSystemId, rule.ConnectedSystemId, typeName);
        }

        return names;
    }

    private static void AddUnlessAmbiguous<TKey>(Dictionary<TKey, string?> namesByKey, TKey key, string typeName)
        where TKey : notnull =>
        namesByKey[key] = namesByKey.TryGetValue(key, out var existing) && existing != typeName ? null : typeName;

    /// <summary>
    /// The name of a Connected System Object Type, where an export rule targets it.
    /// </summary>
    public string? ForObjectType(int? connectedSystemObjectTypeId) =>
        connectedSystemObjectTypeId is { } typeId ? _namesByObjectTypeId.GetValueOrDefault(typeId) : null;

    /// <summary>
    /// The name of the type an export Synchronisation Rule targets: exact for anything that rule provisioned,
    /// disconnected or deprovisioned.
    /// </summary>
    public string? ForSyncRule(int? syncRuleId) =>
        syncRuleId is { } ruleId ? _namesBySyncRuleId.GetValueOrDefault(ruleId) : null;

    /// <summary>
    /// The name of the type a Connected System's export rules for a Metaverse Object Type target, where they all
    /// target one type; null where they target more than one, since the object's own type cannot then be told.
    /// </summary>
    public string? ForMetaverseObjectType(int connectedSystemId, int? metaverseObjectTypeId) =>
        metaverseObjectTypeId is { } mvoTypeId
            ? _namesByMetaverseObjectType.GetValueOrDefault((connectedSystemId, mvoTypeId))
            : null;

    /// <summary>
    /// The name of the one type a Connected System's export rules target; null where they target more than one.
    /// The last resort, for a caller holding no exact key, and safe as one: it never names a type an object might
    /// not be.
    /// </summary>
    public string? ForConnectedSystem(int connectedSystemId) => _namesByConnectedSystemId.GetValueOrDefault(connectedSystemId);

    /// <summary>
    /// The first name the given keys yield, most exact first: the object's own type, the rule that acted on it, the
    /// Metaverse Object Type it was exported for, then the Connected System where that alone is unambiguous.
    /// </summary>
    public string? Resolve(int connectedSystemId, int? objectTypeId = null, int? syncRuleId = null, int? metaverseObjectTypeId = null) =>
        ForObjectType(objectTypeId)
        ?? ForSyncRule(syncRuleId)
        ?? ForMetaverseObjectType(connectedSystemId, metaverseObjectTypeId)
        ?? ForConnectedSystem(connectedSystemId);

    /// <summary>
    /// The type of a Pending Export's target object: from the object where it is loaded, else from the rule that
    /// provisions it (a Create) or the one the caller names (a deprovisioning Delete's), else by the Metaverse Object
    /// Type it was exported for.
    /// </summary>
    public string? ForPendingExport(PendingExport pendingExport, int? syncRuleId = null, int? metaverseObjectTypeId = null) =>
        pendingExport.ConnectedSystemObject?.Type?.Name
        ?? Resolve(pendingExport.ConnectedSystemId,
            objectTypeId: pendingExport.ConnectedSystemObject?.TypeId,
            syncRuleId: syncRuleId ?? pendingExport.ProvisioningSyncRuleId,
            metaverseObjectTypeId: metaverseObjectTypeId);
}
