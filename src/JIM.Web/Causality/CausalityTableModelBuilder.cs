// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JimUtilities = JIM.Utilities.Utilities;

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
            [SourceKey] = new(CausalityTableObjectRole.Source, SourceDisplayName(model), SourceSubtitle(model), SourceHref(model)),
            [IdentityKey] = new(CausalityTableObjectRole.Identity, IdentityDisplayName(model), model.Context.MvoTypeName, IdentityHref(model))
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
    /// <remarks>
    /// A downstream entry starts out named after the Connected System it belongs to, since that is all
    /// any Downstream-lane event is guaranteed to carry (#1519 Table view, D-S8 follow-up: previously the
    /// system name stood in for the object's own name everywhere, including "Everything" mode's Object
    /// column, which read as though the row belonged to the system rather than an object within it). The
    /// moment an event on the same downstream key carries the object's own identity, a Record-kind link
    /// (currently: the CSO a Provisioned outcome created), the entry is upgraded in place so the
    /// Provision parent and its queued export child still land on one entry, whichever of them brought
    /// the identity.
    /// </remarks>
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
                DownstreamPlaceholderName(causalityEvent.SystemName),
                causalityEvent.SystemName ?? "Connected System");
            downstreamOrder.Add(key);
        }

        var recordLink = causalityEvent.Links.FirstOrDefault(l => l.Kind == CausalityEntityKind.Record);
        if (recordLink != null)
            objectMeta[key] = objectMeta[key] with { DisplayName = recordLink.Label, Href = recordLink.Href };

        return key;
    }

    /// <summary>
    /// The name a downstream entry carries until an event reveals the object's own identity: true for
    /// every preview (nothing has been created yet) and for a recorded run's queued-export-only outcomes
    /// (a Pending Export names its queue, not the object it will create).
    /// </summary>
    private static string DownstreamPlaceholderName(string? systemName) =>
        systemName != null ? $"New object in {systemName}" : "New downstream object";

    /// <summary>
    /// The curated object-level row for an outcome type, or null for a type the Table view leaves to
    /// its attribute rows alone (import/export execution outcomes, confirmations, drift): those carry
    /// their story entirely through the values that changed, and a headline row for them would repeat
    /// what the attribute rows already say.
    /// </summary>
    /// <remarks>
    /// The two Via resolutions below are used deliberately, not interchangeably. The
    /// provisioning/export family (Provision, Deprovision, Export queued) uses the <em>effective</em>
    /// pair, because a queued export or deprovision genuinely IS the Provisioned/export decision's own
    /// rule continuing (#1519 Table view fix 4: production never stamps a Pending Export's own
    /// SyncRuleId, only its Provisioned parent's). The Identity "fate" family (Scope, Projection,
    /// Join, Disconnect, Delete) uses the event's <em>own</em> rule only, exactly as
    /// MvoDeletionScheduled already did before this change: a Deletion Rule or import-scope decision is
    /// never the Synchronisation Rule that happened to run earlier in the same tree, and crediting one
    /// (proven by <c>Build_LeaverItem_GroupsEachDeprovisioningTargetAsItsOwnDownstreamObject</c>, which
    /// failed against effective resolution here) would misattribute the deletion to an unrelated inbound
    /// rule.
    /// </remarks>
    private static CausalityTableRow? BuildObjectLevelRow(
        CausalityEvent causalityEvent, ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string objectKey)
    {
        var (ownVia, ownSyncRuleId) = ResolveViaOwnOnly(causalityEvent);
        var (effectiveVia, effectiveSyncRuleId) = ResolveVia(causalityEvent);
        // An export or deprovision with no rule of its own and nothing same-lane to inherit from (an update
        // export on an existing object, a cascade's deprovision) is credited to the one rule its value
        // changes agree on; values from several rules leave it unattributed rather than guessed.
        if (effectiveSyncRuleId is null && string.IsNullOrWhiteSpace(effectiveVia))
            (effectiveVia, effectiveSyncRuleId) = RuleTheValueChangesAgreeOn(causalityEvent);

        return outcomeType switch
        {
            // Before/After are for attribute values only (#1519 Table view fix 7): every one of these
            // is an object-level fact, and Row() below leaves Current and WouldBe null for all of them.
            // The Outcome column's label already says what happened ("Identity created", "Left scope",
            // "Deprovisioned", ...); only MvoDeletionScheduled loses information by going silent on
            // Current/WouldBe, since its schedule/grace reasoning lived nowhere else, so that one case
            // alone carries it forward as an OutcomeDetail line instead.
            ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Scope, null, ownVia, ownSyncRuleId),

            ActivityRunProfileExecutionItemSyncOutcomeType.Projected => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Projection, null, ownVia, ownSyncRuleId),

            ActivityRunProfileExecutionItemSyncOutcomeType.Joined => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Join, null, ownVia, ownSyncRuleId),

            // A rejoin cancels the object's scheduled deletion: it is a Join, not a Projection, since
            // the Identity already existed.
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Join, null, ownVia, ownSyncRuleId),

            ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Disconnect, null, ownVia, ownSyncRuleId),

            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Delete, null, ownVia, ownSyncRuleId),

            // Via here is the Synchronisation Rule attribution alone (ordinarily none, since a Deletion
            // Rule decision is not a Synchronisation Rule's), and deliberately the event's own rule, not
            // the effective one. The schedule/grace reasoning is the one piece of information Current/
            // WouldBe used to carry that the Outcome label ("Identity deletion scheduled") does not
            // already state, so it survives as an OutcomeDetail line instead of going silent.
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Delete, null,
                string.IsNullOrWhiteSpace(causalityEvent.SyncRuleName) ? null : causalityEvent.SyncRuleName,
                causalityEvent.SyncRuleId,
                !string.IsNullOrWhiteSpace(causalityEvent.DetailMessage)
                    ? causalityEvent.DetailMessage
                    : "Scheduled for deletion"),

            ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Deprovision, null, effectiveVia, effectiveSyncRuleId),

            ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Deprovision, null, effectiveVia, effectiveSyncRuleId),

            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.Provision, null, effectiveVia, effectiveSyncRuleId),

            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.ExportQueued, null, effectiveVia, effectiveSyncRuleId),

            ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.NoContributor, AttributeSubject(causalityEvent), null, null),

            ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved => Row(
                causalityEvent, objectKey, CausalityTableChangeKind.ValuesPreserved, AttributeSubject(causalityEvent), null, null),

            _ => null
        };
    }

    /// <summary>
    /// Builds an object-level row: Current and WouldBe are always null, since those columns state
    /// attribute values and every row built here states an object-level fact instead (#1519 Table view
    /// fix 7). <paramref name="outcomeDetail"/> is the one exception that needs to say more than its
    /// <see cref="CausalityEvent.Label"/> already does (MvoDeletionScheduled's grace reasoning); every
    /// other case leaves it null.
    /// </summary>
    private static CausalityTableRow Row(
        CausalityEvent causalityEvent, string objectKey, CausalityTableChangeKind kind, string? attribute,
        string? via, int? syncRuleId, string? outcomeDetail = null)
    {
        return new CausalityTableRow(objectKey, kind, attribute, null, null, via, syncRuleId,
            causalityEvent.Label, causalityEvent.Tone, outcomeDetail);
    }

    /// <summary>
    /// One row per attribute value change the event itself carries (its own <see cref="CausalityEvent.AttributeRows"/>,
    /// already normalised by <see cref="CausalityModelBuilder"/>), regardless of whether the event also
    /// produced an object-level row above: an Attribute Flow event never does, a Pending Export
    /// staging event always does, and both carry their own value changes just the same.
    /// </summary>
    private static IEnumerable<CausalityTableRow> BuildAttributeRows(CausalityEvent causalityEvent, string objectKey)
    {
        var (eventVia, eventSyncRuleId) = ResolveVia(causalityEvent);

        foreach (var attributeRow in causalityEvent.AttributeRows)
        {
            var current = attributeRow.Operation == CausalityAttributeOperation.Remove
                ? attributeRow.Value
                : attributeRow.PreviousValue;
            var wouldBe = attributeRow.Operation == CausalityAttributeOperation.Remove ? null : attributeRow.Value;

            // A value change that recorded its own contributing rule is the truth for that row (one export
            // or flow can carry values from several rules); only a value with no recorded rule falls back
            // to the event's own or inherited rule.
            var (via, syncRuleId) = string.IsNullOrWhiteSpace(attributeRow.SyncRuleName)
                ? (eventVia, eventSyncRuleId)
                : (attributeRow.SyncRuleName, attributeRow.SyncRuleId);

            yield return new CausalityTableRow(objectKey, CausalityTableChangeKind.AttributeChange,
                attributeRow.Name, current, wouldBe, via, syncRuleId, causalityEvent.Label,
                causalityEvent.Tone);
        }
    }

    /// <summary>
    /// What decided this row, and the Synchronisation Rule to link it to: the event's effective
    /// Synchronisation Rule (its own, or the nearest ancestor's; see
    /// <see cref="CausalityEvent.EffectiveSyncRuleId"/>) where one was recorded, else the event's own
    /// detail message where it carries plain reasoning text (never a bare numeric id: a Downstream-lane
    /// event's detail message can still hold its unparsed target system id for an outcome type the
    /// recorded and speculative builders do not scrub it from, e.g. a cascade's plain Disconnected
    /// child). Reasoning text carries no Synchronisation Rule id.
    /// </summary>
    private static (string? Via, int? SyncRuleId) ResolveVia(CausalityEvent causalityEvent)
    {
        if (!string.IsNullOrWhiteSpace(causalityEvent.EffectiveSyncRuleName))
            return (causalityEvent.EffectiveSyncRuleName, causalityEvent.EffectiveSyncRuleId);

        var detail = causalityEvent.DetailMessage;
        return !string.IsNullOrWhiteSpace(detail) && !detail.All(char.IsAsciiDigit) ? (detail, null) : (null, null);
    }

    /// <summary>
    /// The single Synchronisation Rule every one of the event's recorded value changes names, or nothing
    /// when they name none or more than one.
    /// </summary>
    private static (string? Via, int? SyncRuleId) RuleTheValueChangesAgreeOn(CausalityEvent causalityEvent)
    {
        var rules = causalityEvent.AttributeRows
            .Where(r => r.SyncRuleId.HasValue && !string.IsNullOrWhiteSpace(r.SyncRuleName))
            .Select(r => (r.SyncRuleName, r.SyncRuleId))
            .Distinct()
            .ToList();
        return rules.Count == 1 ? rules[0] : (null, null);
    }

    /// <summary>
    /// The event's own Synchronisation Rule attribution only, never an ancestor's: for the Identity
    /// "fate" rows (Scope, Projection, Join, Disconnect, Delete), inheriting a rule that merely ran
    /// earlier in the same tree would credit it with a Deletion Rule or import-scope decision it did not
    /// make. See <see cref="BuildObjectLevelRow"/>'s remarks for why these rows do not use
    /// <see cref="ResolveVia"/>.
    /// </summary>
    private static (string? Via, int? SyncRuleId) ResolveViaOwnOnly(CausalityEvent causalityEvent)
    {
        if (!string.IsNullOrWhiteSpace(causalityEvent.SyncRuleName))
            return (causalityEvent.SyncRuleName, causalityEvent.SyncRuleId);

        var detail = causalityEvent.DetailMessage;
        return !string.IsNullOrWhiteSpace(detail) && !detail.All(char.IsAsciiDigit) ? (detail, null) : (null, null);
    }

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
    /// The object being synchronised's own Connected System Object page (#1519 Table view fix 6): built
    /// from the page context's record id and the Connected System it lives on (never the run's own
    /// system; see <see cref="CausalityPageContext.CsoConnectedSystemId"/>'s remarks on the two not being
    /// interchangeable). Null when either half is unresolved, e.g. a record that has since been deleted.
    /// </summary>
    private static string? SourceHref(CausalityModel model) =>
        model.Context.CsoId is { } csoId && csoId != Guid.Empty && model.Context.CsoConnectedSystemId is { } systemId
            ? JimUtilities.GetConnectedSystemObjectHref(systemId, csoId)
            : null;

    /// <summary>
    /// The Identity's own Metaverse Object page (#1519 Table view fix 6): the href carried by the first
    /// Identity-kind link with one among the model's Identity-lane events, the same link
    /// <see cref="IdentityDisplayName"/> reads its name from. Null where no event yet names a linkable
    /// Identity (a speculative preview whose join has no Identity of its own to point at yet) or where the
    /// Identity has since been deleted (its link then points at the deletion record instead, carrying no
    /// href of its own kind).
    /// </summary>
    private static string? IdentityHref(CausalityModel model) =>
        model.AllEvents()
            .Where(e => e.Lane == CausalityLane.Identity)
            .SelectMany(e => e.Links)
            .FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity && l.Href != null)
            ?.Href;

    /// <summary>
    /// The Metaverse Object's display name: the name an Identity-lane event's own link names it by,
    /// where one exists (the same name the recorded and speculative link builders use for the Metaverse
    /// Object itself), else the record's own name, since the two are ordinarily the same person or object.
    /// </summary>
    private static string IdentityDisplayName(CausalityModel model)
    {
        var linkedName = model.AllEvents()
            .Where(e => e.Lane == CausalityLane.Identity)
            .SelectMany(e => e.Links)
            .FirstOrDefault(l => l.Kind == CausalityEntityKind.Identity)
            ?.Label;

        // Never the object's own label: a joined object's preview knows the Metaverse Object exists but
        // not its name, and showing the object's name twice reads as though the object and the Metaverse
        // Object were one.
        return linkedName ?? "Metaverse Object";
    }

    private static List<CausalityTableObject> BuildObjects(
        Dictionary<string, ObjectMeta> objectMeta, List<string> downstreamOrder, IReadOnlyList<CausalityTableRow> rows)
    {
        var orderedKeys = new List<string> { SourceKey, IdentityKey };
        orderedKeys.AddRange(downstreamOrder);

        var objects = new List<CausalityTableObject> { BuildObject(EverythingKey, CausalityTableObjectRole.Everything,
            "Everything", null, null, rows) };

        objects.AddRange(orderedKeys.Select(key =>
        {
            var meta = objectMeta[key];
            return BuildObject(key, meta.Role, meta.DisplayName, meta.Subtitle, meta.Href, rows);
        }));

        return objects;
    }

    private static CausalityTableObject BuildObject(
        string key, CausalityTableObjectRole role, string displayName, string? subtitle, string? href,
        IReadOnlyList<CausalityTableRow> allRows)
    {
        var rows = role == CausalityTableObjectRole.Everything ? allRows : allRows.Where(r => r.ObjectKey == key).ToList();
        var tone = rows.Count > 0 ? rows.Select(r => r.Tone).OrderByDescending(SeverityRank).First() : CausalityTone.Secondary;
        return new CausalityTableObject(key, role, displayName, subtitle, tone, rows.Count, href);
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

    private sealed record ObjectMeta(CausalityTableObjectRole Role, string DisplayName, string? Subtitle, string? Href = null);
}
