// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
using JIM.Models.Utility;

namespace JIM.Web.Causality;

/// <summary>
/// Reads everything the causality panel needs for one execution item (#2100). The item page and the Causality Explorer
/// both render an item's panel, so both read it here, and the panel stays a rendering component with no data access of
/// its own.
/// </summary>
public static class CausalityItemLoader
{
    /// <summary>
    /// The outcome types whose <c>TargetEntityId</c> is the Metaverse Object's own id. Only these are used to
    /// look the Identity up, because a null lookup is read as proof the object is gone, and reading it off an
    /// outcome that targets something else would badge a live object as deleted.
    /// </summary>
    private static readonly ActivityRunProfileExecutionItemSyncOutcomeType[] IdentityTargetingOutcomes =
    [
        ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
        ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
        ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected,
        ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
        ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
        ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled
    ];

    /// <summary>
    /// Whether the item has a causality story to show: something it recorded, or an export held for a decision about a
    /// generated value (#242, release 4), which records nothing because nothing was exported, so its band says why.
    /// </summary>
    public static bool ShowsCausality(ActivityRunProfileExecutionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.SyncOutcomes.Count > 0 || item.ErrorType == ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved;
    }

    /// <summary>
    /// Reads an execution item's causality.
    /// </summary>
    /// <param name="jim">The application to read through, owned by the caller.</param>
    /// <param name="runProfileExecutionItemId">The item.</param>
    /// <param name="links">Where the panel's links go; the item page's own addresses where null.</param>
    /// <returns>The item's causality, or null where the item does not exist.</returns>
    public static async Task<CausalityItemData?> LoadAsync(JimApplication jim, Guid runProfileExecutionItemId, ICausalityLinks? links = null)
    {
        var detail = await jim.Activities.GetActivityRunProfileExecutionItemDetailAsync(runProfileExecutionItemId);
        if (detail == null)
            return null;

        var item = detail.Item;

        var activity = await jim.Activities.GetActivityAsync(item.ActivityId);

        var connectedSystemId = item.GetConnectedSystemId() ?? activity?.ConnectedSystemId;
        var recordConnectedSystem = connectedSystemId is { } recordConnectedSystemId
            ? await jim.ConnectedSystems.GetConnectedSystemHeaderAsync(recordConnectedSystemId)
            : null;

        // The run's own Connected System (the system the Run Profile executed against), independent of
        // which system the item's Connected System Object belongs to. These normally coincide, but
        // diverge for cross-system cascades (e.g. a Full Sync on system A provisioning/exporting to a
        // CSO in system B): the Causality panel and Execution Summary describe the run, not the object,
        // so they use this rather than the record's. Degrades to null, never throws.
        ConnectedSystemHeader? runConnectedSystem = null;
        if (activity?.ConnectedSystemId is { } runConnectedSystemId)
        {
            runConnectedSystem = runConnectedSystemId == connectedSystemId
                ? recordConnectedSystem
                : await jim.ConnectedSystems.GetConnectedSystemHeaderAsync(runConnectedSystemId);
        }

        var livePendingExportIds = await LoadLivePendingExportIdsAsync(jim, item);
        var provisionedConnectedSystemObjectNames = await LoadProvisionedConnectedSystemObjectNamesAsync(jim, item);

        // Only items with outcomes render the causality panel, and the chain renders inside it, so an item
        // without outcomes has nowhere to show a chain and the walk is not worth paying for.
        var chain = item.SyncOutcomes.Count > 0
            ? await jim.Activities.GetCausalChainAsync(runProfileExecutionItemId)
            : null;

        var (identityHeader, deletedIdentityId) = await ResolveIdentityAsync(jim, item);
        var changeValues = await LoadChangeValuesAsync(jim, item, detail.ChangeValueSummaries);

        return new CausalityItemData
        {
            Item = item,
            Activity = activity,
            RecordConnectedSystem = recordConnectedSystem,
            RunConnectedSystem = runConnectedSystem,
            ExternalIdAttributeValue = item.GetExternalIdAttributeValue(),
            // Parse the decision-time deletion policy snapshot (#119); null for legacy records (which fall
            // back to current-configuration rendering) and for items with no deletion rule evaluation.
            DeletionPolicySnapshot = MvoDeletionPolicySnapshot.FromJson(item.DeletionPolicySnapshotJson),
            LivePendingExportIds = livePendingExportIds,
            ProvisionedConnectedSystemObjectNames = provisionedConnectedSystemObjectNames,
            Chain = chain,
            ChangeValues = changeValues,
            IdentityHeader = identityHeader,
            DeletedIdentityId = deletedIdentityId,
            MetaverseObjectType = await ResolveMetaverseObjectTypeAsync(jim, item),
            Links = links ?? CausalityPageLinks.Instance,
            FollowUps = (await jim.Activities.GetExecutionItemFollowUpsAsync(item.Id))
                .Select(JIM.Web.Models.ExecutionItemFollowUpDisplay.From)
                .ToList()
        };
    }

    /// <summary>
    /// Builds the causality panel's page context from what was read, falling back to the item's snapshot fields when
    /// the Connected System Object has been deleted. ConnectedSystemId/Name carry the run's own Connected System;
    /// CsoConnectedSystemId/Name carry the record's own Connected System. The two normally coincide but diverge for
    /// cross-system cascades (e.g. a Full Sync on system A provisioning or exporting to a CSO on system B); see
    /// <see cref="CausalityPageContext"/> for which consumers need which identity.
    /// </summary>
    public static CausalityPageContext BuildContext(CausalityItemData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // An item that records a Metaverse Object (an export scope review, #1971) has no record of its own: its name
        // snapshot is the Metaverse Object's, and the record system resolved for it is merely the run's. Passed as a
        // record, the panel drew an object that took no part in the run (#1982), so the Metaverse Object is passed as
        // the subject instead.
        var item = data.Item;
        if (item is { ConnectedSystemObject: null, MetaverseObjectId: { } subjectId })
        {
            return new CausalityPageContext(
                ConnectedSystemId: data.RunConnectedSystem?.Id,
                ConnectedSystemName: data.RunConnectedSystem?.Name,
                RunProfileName: data.Activity?.TargetName,
                CsoId: null,
                CsoConnectedSystemId: null,
                CsoConnectedSystemName: null,
                CsoDisplayName: null,
                CsoExternalId: null,
                CsoObjectTypeName: null,
                MvoTypeName: data.MetaverseObjectType?.Name ?? data.IdentityHeader?.TypeName ?? item.ObjectTypeSnapshot,
                MvoTypePluralName: data.MetaverseObjectType?.PluralName ?? data.IdentityHeader?.TypePluralName,
                DeletedMetaverseObjectId: data.DeletedIdentityId,
                ConnectedSystemObjectNames: data.ProvisionedConnectedSystemObjectNames,
                SubjectMetaverseObjectId: subjectId,
                SubjectMetaverseObjectName: data.IdentityHeader?.NameOrId ?? item.DisplayNameSnapshot)
            {
                Links = data.Links
            };
        }

        return new CausalityPageContext(
            ConnectedSystemId: data.RunConnectedSystem?.Id,
            ConnectedSystemName: data.RunConnectedSystem?.Name,
            RunProfileName: data.Activity?.TargetName,
            CsoId: item.ConnectedSystemObjectId,
            CsoConnectedSystemId: data.RecordConnectedSystem?.Id,
            CsoConnectedSystemName: data.RecordConnectedSystem?.Name,
            CsoDisplayName: item.ConnectedSystemObject?.NameOrId ?? item.DisplayNameSnapshot,
            CsoExternalId: data.ExternalIdAttributeValue?.ToStringNoName() ?? item.ExternalIdSnapshot,
            CsoObjectTypeName: item.ConnectedSystemObject?.Type?.Name ?? item.ObjectTypeSnapshot,
            MvoTypeName: data.MetaverseObjectType?.Name ?? data.IdentityHeader?.TypeName,
            MvoTypePluralName: data.MetaverseObjectType?.PluralName ?? data.IdentityHeader?.TypePluralName,
            DeletedMetaverseObjectId: data.DeletedIdentityId,
            ConnectedSystemObjectNames: data.ProvisionedConnectedSystemObjectNames)
        {
            Links = data.Links
        };
    }

    /// <summary>
    /// The id of the Identity the item is about: the Metaverse Object it names, where it records one directly (an
    /// export scope review, #1971, whose outcomes target the accounts it acted on rather than the object), otherwise
    /// the object its identity-targeting outcome points at.
    /// </summary>
    public static Guid? ResolveIdentityId(ActivityRunProfileExecutionItem item) =>
        item.MetaverseObjectId ?? item.SyncOutcomes
            .Where(o => IdentityTargetingOutcomes.Contains(o.OutcomeType))
            .Select(o => o.TargetEntityId)
            .FirstOrDefault(id => id.HasValue && id.Value != Guid.Empty);

    /// <summary>
    /// Looks the item's Identity up once, by id, and returns what came back.
    /// </summary>
    /// <remarks>
    /// One header read answers both of the causality panel's questions about the Identity. Found: it carries
    /// the type plural name the Metaverse Object route is keyed on, so the panel can link it even where the
    /// type resolution below fails (which it does whenever the item's record has since been deleted, because
    /// that is where the chain starts). Not found: the Identity has been deleted, by this run or a later one,
    /// so the panel can say so and offer its deletion record instead of a name that merely does not link.
    /// A header is the light read for this; the object's full graph is not needed to answer either question.
    /// </remarks>
    private static async Task<(MetaverseObjectHeader? Header, Guid? DeletedId)> ResolveIdentityAsync(
        JimApplication jim, ActivityRunProfileExecutionItem item)
    {
        if (ResolveIdentityId(item) is not { } id)
            return (null, null);

        var header = await jim.Metaverse.GetMetaverseObjectHeaderAsync(id);
        return header == null ? (null, id) : (header, null);
    }

    /// <summary>
    /// Resolves the Metaverse Object Type for headings and deletion rule display.
    /// </summary>
    private static async Task<MetaverseObjectType?> ResolveMetaverseObjectTypeAsync(JimApplication jim, ActivityRunProfileExecutionItem item)
    {
        // Try to get the MVO type from the CSO's linked MetaverseObject, or from Synchronisation Rules
        int? mvoTypeId = null;
        int? csoTypeId = null;

        // First try: get from the CSO's linked MetaverseObject (if CSO still exists and is joined)
        if (item.ConnectedSystemObject?.MetaverseObject?.Type is { } joinedType)
        {
            mvoTypeId = joinedType.Id;
        }

        // Second try: get CSO type ID from live CSO or from DeletedObjectType snapshot
        if (item.ConnectedSystemObject is { TypeId: > 0 } liveCso)
        {
            csoTypeId = liveCso.TypeId;
        }
        else if (item.ConnectedSystemObjectChange?.DeletedObjectType is { Id: > 0 } deletedObjectType)
        {
            // CSO was deleted - use the preserved type information from the change record
            csoTypeId = deletedObjectType.Id;
        }

        // Third try: for disconnected RPEIs, the MVO ID is stored on the sync outcome's TargetEntityId
        // (the CSO-MVO join is broken after disconnection, so the first try won't work)
        if (!mvoTypeId.HasValue
            && item.ObjectChangeType == ObjectChangeType.Disconnected)
        {
            var disconnectedOutcome = item.SyncOutcomes
                .FirstOrDefault(o => o.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected
                    or ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);
            if (disconnectedOutcome?.TargetEntityId is { } disconnectedMetaverseObjectId
                && await jim.Metaverse.GetMetaverseObjectHeaderAsync(disconnectedMetaverseObjectId) is { } header)
            {
                mvoTypeId = header.TypeId;
            }
        }

        // Fourth try: if we don't have MVO type yet but have CSO type, look up via Synchronisation Rules
        if (!mvoTypeId.HasValue && csoTypeId.HasValue)
        {
            var syncRules = await jim.ConnectedSystems.GetSyncRulesAsync();
            var syncRule = syncRules.FirstOrDefault(sr => sr.ConnectedSystemObjectTypeId == csoTypeId.Value);
            if (syncRule != null)
            {
                mvoTypeId = syncRule.MetaverseObjectTypeId;
            }
        }

        // The object type supplies its name for panel headings only. Authoritative source names come
        // from the decision-time policy snapshot (#119), so no per-source lookup is needed here.
        return mvoTypeId.HasValue
            ? await jim.Metaverse.GetMetaverseObjectTypeAsync(mvoTypeId.Value, false)
            : null;
    }

    /// <summary>
    /// The item's change values (#2498): the summary of each change record, and the rows of every change small enough to
    /// carry whole, read in one window per side. A change to a large group is left to be read a window at a time where
    /// it is shown, so the page never loads one value per member.
    /// </summary>
    private static async Task<CausalityChangeValues> LoadChangeValuesAsync(
        JimApplication jim, ActivityRunProfileExecutionItem item, IReadOnlyDictionary<Guid, ChangeValueSummary> summaries)
    {
        var small = summaries.Values.Where(s => s.RowCount <= CausalityChangeValues.InlineLimit).ToList();
        var metaverseChangeId = item.MetaverseObjectChange?.Id;
        var smallMetaverse = small.Where(s => s.ChangeId == metaverseChangeId).ToList();
        var smallConnectedSystem = small.Where(s => s.ChangeId != metaverseChangeId).ToList();

        var rows = new Dictionary<Guid, IReadOnlyList<CausalityAttributeRow>>();
        if (smallConnectedSystem.Count > 0)
        {
            var read = await jim.ConnectedSystems.GetCsoChangeValueRowsAsync(WholeChangesQuery(smallConnectedSystem));
            foreach (var group in read.Results.GroupBy(r => r.ChangeId))
                rows[group.Key] = group.Select(CausalityAttributeRowMapper.FromRow).ToList();
        }

        if (smallMetaverse.Count > 0)
        {
            var read = await jim.Metaverse.GetMvoChangeValueRowsAsync(WholeChangesQuery(smallMetaverse));
            foreach (var group in read.Results.GroupBy(r => r.ChangeId))
                rows[group.Key] = group.Select(CausalityAttributeRowMapper.FromRow).ToList();
        }

        return new CausalityChangeValues { Summaries = summaries, Rows = rows };
    }

    /// <summary>Every row of the given small changes, in source order: one window holds them all.</summary>
    private static ChangeValueRowQuery WholeChangesQuery(IReadOnlyCollection<ChangeValueSummary> changes) => new()
    {
        ChangeIds = changes.Select(c => c.ChangeId).ToList(),
        Count = changes.Sum(c => c.RowCount)
    };

    /// <summary>
    /// Resolves which of the Pending Exports this item created are still queued. A Pending Export is
    /// deleted once it has been exported, while the item page is a permanent record of the run that
    /// created it, so without this every Pending Export link on an item older than the next export run
    /// would land on "Pending Export not found". One projection query per load, bounded by the
    /// number of Pending Export outcomes on this one item.
    /// </summary>
    private static async Task<IReadOnlySet<Guid>> LoadLivePendingExportIdsAsync(JimApplication jim, ActivityRunProfileExecutionItem item)
    {
        var pendingExportIds = item.SyncOutcomes
            .Where(o => SyncOutcomeTypes.IsPendingExport(o.OutcomeType)
                        && o.TargetEntityId is { } id && id != Guid.Empty)
            .Select(o => o.TargetEntityId!.Value)
            .Distinct()
            .ToList();

        // An empty set, not null, where the item names no Pending Export: there is nothing to downgrade, and null
        // would read as "the lookup did not run".
        if (pendingExportIds.Count == 0)
            return new HashSet<Guid>();

        var live = await jim.ConnectedSystems.GetExistingPendingExportIdsAsync(pendingExportIds);
        return live.ToHashSet();
    }

    /// <summary>
    /// Resolves the current display label of every Connected System Object this item's tree Provisioned
    /// (#1519 Table view fix 5), so the causality panel can show the object's name as it stands now
    /// rather than the "type: id" label a recorded run captured at creation time, before the object had
    /// an external id. A Provisioned outcome's own <c>DetailMessage</c> carries its target Connected
    /// System's id (see <see cref="OutcomeDetailMessageParser"/>); PendingExportCreated children under a
    /// Provisioned parent target the same object, so only the Provisioned outcomes themselves need
    /// collecting. Bounded by the number of Provisioned outcomes on this one item, and grouped by
    /// Connected System since a single item can provision to several targets.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, string>?> LoadProvisionedConnectedSystemObjectNamesAsync(
        JimApplication jim, ActivityRunProfileExecutionItem item)
    {
        // A Provisioned outcome's target is the object it created; a queued export's or deprovision's target
        // is the Pending Export, so the object comes from the change record beneath it (an update to an
        // existing object names it there; a provisioning export's change may name the new object too).
        var provisionedByConnectedSystem = item.SyncOutcomes
            .Select(o => (
                ConnectedSystemId: OutcomeDetailMessageParser.Parse(o.DetailMessage).ConnectedSystemId,
                CsoId: o.OutcomeType switch
                {
                    ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
                        or ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected
                        or ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn => o.TargetEntityId,
                    ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
                        or ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued => o.ConnectedSystemObjectChange?.ConnectedSystemObjectId,
                    _ => null
                }))
            .Where(t => t.ConnectedSystemId.HasValue && t.CsoId is { } id && id != Guid.Empty)
            .GroupBy(t => t.ConnectedSystemId!.Value, t => t.CsoId!.Value)
            .ToList();

        if (provisionedByConnectedSystem.Count == 0)
            return null;

        // Named without reading the objects' values (#2498): a provisioned group's own values are its members. A
        // deleted-since object simply has no entry: the recorded "type: id" label stands.
        return await jim.ConnectedSystems.GetConnectedSystemObjectNamesAsync(
            provisionedByConnectedSystem.SelectMany(group => group).Distinct().ToList());
    }
}
