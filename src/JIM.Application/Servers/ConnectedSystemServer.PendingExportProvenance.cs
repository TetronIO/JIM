// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Application.Servers;

/// <summary>
/// Pending Export value provenance (#399's "Value from" column): where the value queued for each Connected
/// System attribute originated, resolved through the export mapping the staging Synchronisation Rule used. Kept
/// as its own partial file (matching the <c>MetaverseServer.Provenance.cs</c> convention) so
/// <see cref="ConnectedSystemServer"/>'s main file stays uncluttered.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Resolves, per Connected System attribute, where the value queued on a Pending Export originated: a single
    /// Metaverse attribute passed through unchanged (with the origin of the source Metaverse Object's current
    /// value for it, reusing <see cref="MetaverseServer.GetMetaverseObjectProvenanceAsync"/> so this can never
    /// disagree with the Inspect view), or a computed source (an expression, an advanced/chained mapping, or
    /// Unique Value Generation). One query set for the whole Pending Export: one mapping lookup and one
    /// provenance lookup against the source Metaverse Object, never one per attribute row.
    /// </summary>
    /// <param name="pendingExport">
    /// The Pending Export to resolve, with <see cref="PendingExport.AttributeValueChanges"/> loaded (its
    /// <c>Attribute</c> navigation is not required; only the scalar <c>AttributeId</c> and <c>SyncRuleId</c> are
    /// read).
    /// </param>
    /// <returns>
    /// One entry per distinct Connected System attribute the loaded changes carry whose export mapping could be
    /// resolved. Empty when the Pending Export has no source Metaverse Object (a delete), or when none of the
    /// loaded changes carry a resolvable Synchronisation Rule.
    /// </returns>
    public async Task<List<PendingExportValueSource>> GetPendingExportValueSourcesAsync(PendingExport pendingExport)
    {
        if (!pendingExport.SourceMetaverseObjectId.HasValue)
            return new List<PendingExportValueSource>();

        // One (Synchronisation Rule, Connected System attribute) pair per distinct staged attribute; several
        // value changes for a multi-valued attribute all point at the same mapping.
        var pairs = pendingExport.AttributeValueChanges
            .Where(c => c.SyncRuleId.HasValue)
            .Select(c => (SyncRuleId: c.SyncRuleId!.Value, AttributeId: c.AttributeId))
            .Distinct()
            .ToList();

        if (pairs.Count == 0)
            return new List<PendingExportValueSource>();

        var syncRuleIds = pairs.Select(p => p.SyncRuleId).Distinct().ToList();
        var attributeIds = pairs.Select(p => p.AttributeId).Distinct().ToList();

        var mappings = await Application.Repository.ConnectedSystems.GetExportSyncRuleMappingsForTargetsAsync(syncRuleIds, attributeIds);
        var mappingsByTarget = mappings
            .Where(m => m.TargetConnectedSystemAttributeId.HasValue)
            .ToDictionary(m => (m.SyncRuleId, m.TargetConnectedSystemAttributeId!.Value));

        // Every single-Metaverse-attribute source resolved in one provenance lookup against the source Metaverse
        // Object, rather than a query per attribute; the same resolution the Inspect view uses, so a value can
        // never read differently on the two pages.
        var provenance = await Application.Metaverse.GetMetaverseObjectProvenanceAsync(pendingExport.SourceMetaverseObjectId.Value);
        var originsByAttributeId = provenance?.Attributes.ToDictionary(a => a.AttributeId)
            ?? new Dictionary<int, MetaverseAttributeOriginSummary>();

        var results = new List<PendingExportValueSource>();
        foreach (var (syncRuleId, attributeId) in pairs.Where(p => mappingsByTarget.ContainsKey((p.SyncRuleId, p.AttributeId))))
        {
            var mapping = mappingsByTarget[(syncRuleId, attributeId)];
            results.Add(BuildValueSource(attributeId, mapping, originsByAttributeId));
        }

        return results;
    }

    /// <summary>
    /// Classifies one export mapping's source: a single Metaverse attribute passed through unchanged (resolved
    /// against the provenance summary already looked up for the whole Pending Export), or a computed source.
    /// </summary>
    private static PendingExportValueSource BuildValueSource(
        int connectedSystemAttributeId, SyncRuleMapping mapping, Dictionary<int, MetaverseAttributeOriginSummary> originsByAttributeId)
    {
        var singleSource = mapping.Generation == null
            && mapping.Sources.Count == 1
            && mapping.Sources[0].MetaverseAttributeId.HasValue
            && string.IsNullOrWhiteSpace(mapping.Sources[0].Expression)
                ? mapping.Sources[0]
                : null;

        if (singleSource == null)
        {
            return new PendingExportValueSource
            {
                ConnectedSystemAttributeId = connectedSystemAttributeId,
                IsComputed = true,
                Expression = DescribeComputedSource(mapping)
            };
        }

        var summary = originsByAttributeId.GetValueOrDefault(singleSource.MetaverseAttributeId!.Value);
        return new PendingExportValueSource
        {
            ConnectedSystemAttributeId = connectedSystemAttributeId,
            SourceMetaverseAttributeId = singleSource.MetaverseAttributeId,
            SourceMetaverseAttributeName = singleSource.MetaverseAttribute?.Name,
            Origin = summary?.Origins.FirstOrDefault() ?? ValueOrigin.NotRecorded,
            HasSeveralOrigins = summary?.HasSeveralOrigins ?? false
        };
    }

    /// <summary>
    /// The text shown for a computed source: Unique Value Generation names itself; an expression mapping shows
    /// its expression; an advanced/chained mapping (several sources, or an attribute source combined with
    /// functions) falls back to naming its source attributes, since it has no single expression string of its own.
    /// </summary>
    private static string DescribeComputedSource(SyncRuleMapping mapping)
    {
        if (mapping.Generation != null)
            return "Generated by JIM";

        var expressionSource = mapping.Sources
            .OrderBy(s => s.Order)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Expression));
        if (expressionSource != null)
            return expressionSource.Expression!;

        return string.Join(", ", mapping.Sources
            .OrderBy(s => s.Order)
            .Select(s => s.MetaverseAttribute?.Name ?? $"attribute {s.MetaverseAttributeId}"));
    }
}
