// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Causality;

/// <summary>
/// Builds the Table view's <see cref="CausalityTableModel"/> from a <see cref="CausalityModel"/>
/// (#1519 Phase 3, D-S8): a flat, per-object change list for a recorded Run Profile Execution Item as
/// much as for a Sync Preview. Pure and side-effect free, exactly like <see cref="CausalityModelBuilder"/>
/// itself; the Table view is a second projection of the same tree, never a second source of causality.
/// </summary>
public static class CausalityTableModelBuilder
{
    private const string EverythingKey = "everything";
    private const string SourceKey = "source";
    private const string IdentityKey = "identity";

    /// <summary>
    /// Builds the Table view model. A synthetic event (see <see cref="CausalityEvent.IsSynthetic"/>)
    /// carries no outcome type to classify and is skipped: it explains a decision, not a change.
    /// </summary>
    public static CausalityTableModel Build(CausalityModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var objectMeta = new Dictionary<string, ObjectMeta>
        {
            [SourceKey] = new(CausalityTableObjectRole.Source, SourceDisplayName(model), SourceSubtitle(model)),
            [IdentityKey] = new(CausalityTableObjectRole.Identity, IdentityDisplayName(model), model.Context.MvoTypeName)
        };
        var downstreamOrder = new List<string>();
        var rows = new List<CausalityTableRow>();

        foreach (var causalityEvent in model.AllEvents())
        {
            if (causalityEvent.IsSynthetic || causalityEvent.OutcomeType is not { } outcomeType)
                continue;

            var objectKey = ResolveObjectKey(causalityEvent, objectMeta, downstreamOrder);

            if (BuildObjectLevelRow(causalityEvent, outcomeType, objectKey) is { } objectRow)
                rows.Add(objectRow);

            rows.AddRange(BuildAttributeRows(causalityEvent, objectKey));
        }

        // Object-level rows first, then attribute rows, each group in encounter order: a stable sort
        // partitioning on the one distinction that matters preserves both groups' internal ordering.
        var orderedRows = rows
            .OrderBy(r => r.ChangeKind == CausalityTableChangeKind.AttributeChange ? 1 : 0)
            .ToList();

        var objects = BuildObjects(objectMeta, downstreamOrder, orderedRows);

        return new CausalityTableModel
        {
            Objects = objects,
            Rows = orderedRows,
            CurrentHeading = model.IsSpeculative ? "Current" : "Before",
            NextHeading = model.IsSpeculative ? "Would be" : "After",
            IsSpeculative = model.IsSpeculative
        };
    }

    /// <summary>
    /// Which object an event's rows belong to: the object being synchronised and the Identity are
    /// fixed by <see cref="CausalityEvent.Lane"/>; a Downstream-lane event's target Connected System
    /// (or, once addressable, its own Connected System Object) is discovered as events are walked.
    /// </summary>
    private static string ResolveObjectKey(
        CausalityEvent causalityEvent, Dictionary<string, ObjectMeta> objectMeta, List<string> downstreamOrder)
    {
        if (causalityEvent.Lane != CausalityLane.Downstream)
            return causalityEvent.Lane == CausalityLane.Source ? SourceKey : IdentityKey;

        // Keyed by name first: a Provisioned node names its target system without an id, while its queued
        // export child carries the id, and the two must land on the same entry rather than one each.
        var key = $"ds:{causalityEvent.SystemName ?? (object?)causalityEvent.SystemId ?? "unknown"}";
        if (!objectMeta.ContainsKey(key))
        {
            objectMeta[key] = new ObjectMeta(
                CausalityTableObjectRole.Downstream,
                causalityEvent.SystemName ?? "Downstream system",
                "Connected System");
            downstreamOrder.Add(key);
        }

        return key;
    }

    /// <summary>
    /// The curated object-level row for an outcome type, or null for a type the Table view leaves to
    /// its attribute rows alone (import/export execution outcomes, confirmations, drift): those carry
    /// their story entirely through the values that changed, and a headline row for them would repeat
    /// what the attribute rows already say.
    /// </summary>
    private static CausalityTableRow? BuildObjectLevelRow(
        CausalityEvent causalityEvent, ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string objectKey)
    {
        return outcomeType switch
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Scope, "Import scope",
                "In scope", "Out of scope", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.Projected => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.JoinOrProjection, "Metaverse Object",
                null, "New Identity", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.Joined => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.JoinOrProjection, "Metaverse Object",
                null, "Joined to existing Identity", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.JoinOrProjection, "Metaverse Object",
                "Deletion scheduled", "Rejoined; deletion cancelled", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Disconnect, "Metaverse Object",
                "Joined", "Disconnected", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Delete, "Metaverse Object",
                "Active", "Deleted", Via(causalityEvent)),

            // The schedule/grace reasoning lives in the would-be cell rather than Via, so it is not
            // stated twice; Via here is the Synchronisation Rule attribution alone (ordinarily none,
            // since a Deletion Rule decision is not a Synchronisation Rule's).
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Delete, "Metaverse Object", "Active",
                !string.IsNullOrWhiteSpace(causalityEvent.DetailMessage)
                    ? $"Scheduled: {causalityEvent.DetailMessage}"
                    : "Scheduled for deletion",
                string.IsNullOrWhiteSpace(causalityEvent.SyncRuleName) ? null : causalityEvent.SyncRuleName),

            ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Deprovision, ConnectorSubject(causalityEvent),
                "Provisioned", "Deprovision queued", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Deprovision, ConnectorSubject(causalityEvent),
                "Provisioned", "Deprovisioned", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Provision, ConnectorSubject(causalityEvent),
                null, "Account provisioned", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.ExportQueued, ConnectorSubject(causalityEvent),
                null, "Export queued", Via(causalityEvent)),

            ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.NoContributor, AttributeSubject(causalityEvent),
                "Has a value", "Cleared (no contributor)", null),

            ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.ValuesPreserved, AttributeSubject(causalityEvent),
                "Value", "Preserved (no import source)", null),

            _ => null
        };
    }

    private static CausalityTableRow Row(
        CausalityEvent causalityEvent, string objectKey, CausalityTableChangeKind kind, string attribute,
        string? current, string? wouldBe, string? via)
    {
        return new CausalityTableRow(objectKey, kind, attribute, current, wouldBe, via,
            causalityEvent.PlainLabel, causalityEvent.TechnicalLabel, causalityEvent.Tone);
    }

    /// <summary>
    /// One row per attribute value change the event itself carries (its own <see cref="CausalityEvent.AttributeRows"/>,
    /// already normalised by <see cref="CausalityModelBuilder"/>), regardless of whether the event also
    /// produced an object-level row above: an Attribute Flow event never does, a Pending Export
    /// staging event always does, and both carry their own value changes just the same.
    /// </summary>
    private static IEnumerable<CausalityTableRow> BuildAttributeRows(CausalityEvent causalityEvent, string objectKey)
    {
        var via = Via(causalityEvent);

        foreach (var attributeRow in causalityEvent.AttributeRows)
        {
            var current = attributeRow.Operation == CausalityAttributeOperation.Remove
                ? attributeRow.Value
                : attributeRow.PreviousValue;
            var wouldBe = attributeRow.Operation == CausalityAttributeOperation.Remove ? null : attributeRow.Value;

            yield return new CausalityTableRow(objectKey, CausalityTableChangeKind.AttributeChange,
                attributeRow.Name, current, wouldBe, via, causalityEvent.PlainLabel, causalityEvent.TechnicalLabel,
                causalityEvent.Tone);
        }
    }

    /// <summary>
    /// What decided this row: the attributed Synchronisation Rule where one was recorded, else the
    /// event's own detail message where it carries plain reasoning text (never a bare numeric id: a
    /// Downstream-lane event's detail message can still hold its unparsed target system id for an
    /// outcome type the recorded and speculative builders do not scrub it from, e.g. a cascade's
    /// plain Disconnected child).
    /// </summary>
    private static string? Via(CausalityEvent causalityEvent)
    {
        if (!string.IsNullOrWhiteSpace(causalityEvent.SyncRuleName))
            return causalityEvent.SyncRuleName;

        var detail = causalityEvent.DetailMessage;
        return !string.IsNullOrWhiteSpace(detail) && !detail.All(char.IsAsciiDigit) ? detail : null;
    }

    private static string ConnectorSubject(CausalityEvent causalityEvent) =>
        $"connector: {causalityEvent.SystemName ?? "Connected System"}";

    /// <summary>
    /// The attribute a No Contributor / Values Preserved fact names, drawn from the event's detail
    /// message where it carries one, else a generic subject.
    /// </summary>
    private static string AttributeSubject(CausalityEvent causalityEvent) =>
        string.IsNullOrWhiteSpace(causalityEvent.DetailMessage) ? "Attribute value" : causalityEvent.DetailMessage;

    private static string SourceDisplayName(CausalityModel model) =>
        model.Context.RecordLabel ?? "Object being synchronised";

    private static string? SourceSubtitle(CausalityModel model)
    {
        var parts = new[] { model.Context.CsoConnectedSystemName, model.Context.CsoObjectTypeName }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>
    /// The Identity's display name: the name an Identity-lane event's own link names it by, where one
    /// exists (the same name the recorded and speculative link builders use for the Identity itself),
    /// else the record's own name, since the two are ordinarily the same person or object.
    /// </summary>
    private static string IdentityDisplayName(CausalityModel model)
    {
        var linkedName = model.AllEvents()
            .Where(e => e.Lane == CausalityLane.Identity)
            .SelectMany(e => e.Links)
            .FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity)
            ?.Label;

        // Never the object's own label: a joined object's preview knows the Identity exists but not its
        // name, and showing the object's name twice reads as though the object and the Identity were one.
        return linkedName ?? "Identity";
    }

    private static List<CausalityTableObject> BuildObjects(
        Dictionary<string, ObjectMeta> objectMeta, List<string> downstreamOrder, IReadOnlyList<CausalityTableRow> rows)
    {
        var orderedKeys = new List<string> { SourceKey, IdentityKey };
        orderedKeys.AddRange(downstreamOrder);

        var objects = new List<CausalityTableObject> { BuildObject(EverythingKey, CausalityTableObjectRole.Everything,
            "Everything", null, rows) };

        objects.AddRange(orderedKeys.Select(key =>
        {
            var meta = objectMeta[key];
            return BuildObject(key, meta.Role, meta.DisplayName, meta.Subtitle, rows);
        }));

        return objects;
    }

    private static CausalityTableObject BuildObject(
        string key, CausalityTableObjectRole role, string displayName, string? subtitle,
        IReadOnlyList<CausalityTableRow> allRows)
    {
        var rows = role == CausalityTableObjectRole.Everything ? allRows : allRows.Where(r => r.ObjectKey == key).ToList();
        var tone = rows.Count > 0 ? rows.Select(r => r.Tone).OrderByDescending(SeverityRank).First() : CausalityTone.Secondary;
        return new CausalityTableObject(key, role, displayName, subtitle, tone, rows.Count);
    }

    /// <summary>
    /// Severity rank for the "strongest tone among its rows" roll-up: error, then warning, then
    /// info/primary together, then success/secondary together.
    /// </summary>
    private static int SeverityRank(CausalityTone tone) => tone switch
    {
        CausalityTone.Error => 3,
        CausalityTone.Warning => 2,
        CausalityTone.Info or CausalityTone.Primary => 1,
        _ => 0
    };

    private sealed record ObjectMeta(CausalityTableObjectRole Role, string DisplayName, string? Subtitle);
}
