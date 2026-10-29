// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Recording a saved configuration change as awaiting synchronisation (#2615).
/// <para>
/// A Delta Synchronisation visits only the objects whose own data changed, so saving an import Synchronisation Rule or
/// an Object Matching Rule would reach existing objects only when a Full Synchronisation happened to run. Every save that
/// can change what an import produces therefore records a <see cref="ConfigurationChangeSynchronisation"/> for the
/// Connected System Object Type it affects, and the Connected System's next synchronisation processes every object of
/// that type (see <c>SyncTaskProcessorBase.ClaimConfigurationChangesAwaitingSynchronisationAsync</c>).
/// </para>
/// <para>
/// The row is written AFTER the configuration it describes has been saved, never before and not in the same
/// transaction. A row is claimed by the next synchronisation the moment it is committed, and the claim relies on every
/// claimed row already being part of the rules and Object Types that run loaded; writing it first, or alongside, would
/// let a run claim a change it does not yet contain, and mark it applied.
/// </para>
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Records a Synchronisation Rule save as awaiting synchronisation, when it can change what an import produces.
    /// Called once the rule's save has been written and its change captured, with what the capture found.
    /// <para>
    /// Only import rules record: an export rule's change reaches existing objects through export evaluation, which runs
    /// on every Metaverse Object the import types' synchronisation reprocesses (plan decision 5), so a second mechanism
    /// would be redundant.
    /// </para>
    /// </summary>
    /// <param name="activity">The save's Activity, which the row names as the reason the type is awaiting.</param>
    /// <param name="rule">The rule as saved.</param>
    /// <param name="outcome">What configuration change capture found for the save.</param>
    /// <param name="isNew">Whether the save created the rule.</param>
    /// <exception cref="ConfigurationChangeSynchronisationNotRecordedException">The row could not be recorded. The save's
    /// Activity has been failed with the remedy.</exception>
    private async Task RecordConfigurationChangeAwaitingSynchronisationAsync(
        Activity activity, SyncRule rule, ConfigurationChangeCaptureOutcome outcome, bool isNew)
    {
        if (rule.Direction != SyncRuleDirection.Import)
            return;

        if (!ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(outcome, isInForce: rule.Enabled))
            return;

        await RecordConfigurationChangeAwaitingSynchronisationAsync(
            activity,
            connectedSystemId: rule.ConnectedSystemId > 0 ? rule.ConnectedSystemId : rule.ConnectedSystem?.Id ?? 0,
            connectedSystemName: rule.ConnectedSystem?.Name,
            objectTypeId: rule.ConnectedSystemObjectTypeId > 0 ? rule.ConnectedSystemObjectTypeId : rule.ConnectedSystemObjectType?.Id ?? 0,
            objectTypeName: rule.ConnectedSystemObjectType?.Name,
            syncRuleId: rule.Id > 0 ? rule.Id : null,
            reason: ConfigurationChangeSynchronisationPolicy.Describe(rule.Name, outcome, isNew));
    }

    /// <summary>
    /// Records a Simple Mode Object Matching Rule save as awaiting synchronisation for the Connected System Object Type
    /// the rule is on. The rule belongs to the type, not to a Synchronisation Rule, and the change is captured under the
    /// Connected System, whose snapshot names no type: the type is carried through from the rule.
    /// <para>
    /// Only a type that an enabled import rule covers is recorded. Nothing synchronises the objects of any other type,
    /// so there is nothing to process, and enabling an import rule for the type later is a change of its own.
    /// </para>
    /// </summary>
    private async Task RecordSimpleModeMatchingRuleChangeAwaitingSynchronisationAsync(
        Activity activity, int connectedSystemId, int objectTypeId, ConfigurationChangeCaptureOutcome outcome)
    {
        // The Connected System capture compares the whole system, so a Created outcome (no earlier version) says nothing
        // about whether the rule is new; the matching rule save itself is what is being recorded, and it is in force.
        if (!ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(outcome, isInForce: true))
            return;

        var importRule = (await GetEnabledImportRuleHeadersAsync(connectedSystemId))
            .FirstOrDefault(header => header.ConnectedSystemObjectTypeId == objectTypeId);
        if (importRule == null)
            return;

        var verb = activity.TargetOperationType switch
        {
            ActivityTargetOperationType.Create => "created",
            ActivityTargetOperationType.Delete => "deleted",
            _ => "changed"
        };

        await RecordConfigurationChangeAwaitingSynchronisationAsync(
            activity,
            connectedSystemId,
            connectedSystemName: importRule.ConnectedSystemName,
            objectTypeId,
            objectTypeName: importRule.ConnectedSystemObjectTypeName,
            syncRuleId: null,
            reason: $"{importRule.ConnectedSystemObjectTypeName}: Object Matching Rule {verb}");
    }

    /// <summary>
    /// Records a matching mode switch as awaiting synchronisation for every Connected System Object Type an enabled
    /// import rule covers: the mode decides how the objects of every one of them are matched.
    /// </summary>
    private async Task RecordMatchingModeSwitchAwaitingSynchronisationAsync(
        Activity activity, ConnectedSystem connectedSystem, ObjectMatchingRuleMode newMode, ConfigurationChangeCaptureOutcome outcome)
    {
        if (!ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(outcome, isInForce: true))
            return;

        var modeName = newMode == ObjectMatchingRuleMode.SyncRule ? "Advanced Mode" : "Simple Mode";
        var importRulePerType = (await GetEnabledImportRuleHeadersAsync(connectedSystem.Id))
            .GroupBy(header => header.ConnectedSystemObjectTypeId)
            .Select(group => group.First());

        foreach (var importRule in importRulePerType)
        {
            await RecordConfigurationChangeAwaitingSynchronisationAsync(
                activity,
                connectedSystem.Id,
                connectedSystemName: connectedSystem.Name,
                importRule.ConnectedSystemObjectTypeId,
                objectTypeName: importRule.ConnectedSystemObjectTypeName,
                syncRuleId: null,
                reason: $"{importRule.ConnectedSystemObjectTypeName}: Object Matching Rule mode switched to {modeName}");
        }
    }

    /// <summary>
    /// The changes an Activity is part of (#2615), grouped by the Connected System Object Type they are for: those it
    /// recorded, when it is a configuration change, and those a synchronisation run claimed, when it is a run. Applied or
    /// not. A run's groups also carry the number of objects of the type, which is how many it processed because of them.
    /// The changes are capped at <see cref="ObjectTypeAwaitingSynchronisation.MaxChangesListed"/> across the Activity.
    /// <para>
    /// An Activity of a kind that can be neither (a password change, a housekeeping run) is answered without a query:
    /// the Activity page and the Activity endpoint both ask for every Activity they show.
    /// </para>
    /// </summary>
    public async Task<List<ActivityObjectTypeSynchronisation>> GetConfigurationChangeSynchronisationsForActivityAsync(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.TargetType is not (ActivityTargetType.SynchronisationRule or ActivityTargetType.ConnectedSystem
            or ActivityTargetType.ObjectMatchingRule or ActivityTargetType.ConnectedSystemRunProfile))
            return [];

        var changes = await Application.Repository.ConnectedSystems.GetConfigurationChangeSynchronisationSummariesForActivityAsync(
            activity.Id, ObjectTypeAwaitingSynchronisation.MaxChangesListed);

        var groups = new List<ActivityObjectTypeSynchronisation>();
        foreach (var group in changes.GroupBy(change => (change.ConnectedSystemId, change.ConnectedSystemObjectTypeId)))
            groups.Add(await BuildActivityObjectTypeSynchronisationAsync(activity.Id, group.ToList()));

        return groups;
    }

    private async Task<ActivityObjectTypeSynchronisation> BuildActivityObjectTypeSynchronisationAsync(
        Guid activityId, List<ConfigurationChangeSynchronisationSummary> changes)
    {
        var first = changes[0];

        // Only a run has processed objects because of the changes, and only a run claims them. A configuration change
        // Activity never does, so it is not charged a count it has no use for.
        int? objectCount = changes.Any(change => change.ClaimedByActivityId == activityId)
            ? await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(first.ConnectedSystemId, first.ConnectedSystemObjectTypeId, null)
            : null;

        return new ActivityObjectTypeSynchronisation
        {
            ConnectedSystemId = first.ConnectedSystemId,
            ConnectedSystemName = first.ConnectedSystemName,
            ConnectedSystemObjectTypeId = first.ConnectedSystemObjectTypeId,
            ObjectTypeName = first.ObjectTypeName,
            ObjectCount = objectCount,
            Changes = changes
        };
    }

    /// <summary>
    /// Fills in what each Synchronisation Rule in a list is waiting on: for an import rule, how many saved changes wait for
    /// its Connected System's next synchronisation to process every object of its type; for an export rule, how many
    /// Metaverse Objects of its type are flagged for review against every export rule (#2615). Two reads for the whole
    /// list, however long it is, and none for a kind the list does not hold.
    /// </summary>
    public async Task PopulateSyncRuleReviewStateAsync(IReadOnlyCollection<SyncRuleHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var imports = headers.Where(header => header.Direction == SyncRuleDirection.Import).ToList();
        if (imports.Count > 0)
        {
            var waiting = (await Application.Repository.ConnectedSystems.GetConfigurationChangesAwaitingSynchronisationAsync(
                    imports.Select(header => header.ConnectedSystemId).Distinct().ToList()))
                .ToDictionary(awaiting => (awaiting.ConnectedSystemId, awaiting.ConnectedSystemObjectTypeId), awaiting => awaiting.ChangeCount);
            foreach (var header in imports)
                header.AwaitingSynchronisationChangeCount = waiting.GetValueOrDefault((header.ConnectedSystemId, header.ConnectedSystemObjectTypeId));
        }

        var exports = headers.Where(header => header.Direction == SyncRuleDirection.Export).ToList();
        if (exports.Count > 0)
        {
            var flagged = await Application.Repository.Metaverse.GetExportReviewPendingCountsAsync(
                exports.Select(header => header.MetaverseObjectTypeId).Distinct().ToList());
            foreach (var header in exports)
                header.ExportReviewPendingCount = flagged.GetValueOrDefault(header.MetaverseObjectTypeId);
        }
    }

    // The enabled import rules of one Connected System, as headers: the cheap way to ask which Object Types are
    // synchronised, without loading a rule's graph. The headers span every system, so they are narrowed here.
    private async Task<List<SyncRuleHeader>> GetEnabledImportRuleHeadersAsync(int connectedSystemId)
    {
        var headers = await Application.Repository.ConnectedSystems.GetSyncRuleHeadersAsync(direction: SyncRuleDirection.Import);
        return headers
            .Where(header => header.ConnectedSystemId == connectedSystemId && header.Enabled)
            .ToList();
    }

    /// <summary>
    /// Writes one change awaiting synchronisation. When it cannot be written the failure is loud: the save's Activity is
    /// failed naming the remedy (a Full Synchronisation, which processes every object of every type), and an
    /// <see cref="ConfigurationChangeSynchronisationNotRecordedException"/> carries the cause out. The configuration
    /// change itself stands, so the administrator must be told it may not reach the objects that already exist; reporting
    /// the save as a success would leave them believing it would.
    /// </summary>
    private async Task RecordConfigurationChangeAwaitingSynchronisationAsync(
        Activity activity, int connectedSystemId, string? connectedSystemName, int objectTypeId, string? objectTypeName,
        int? syncRuleId, string reason)
    {
        var change = new ConfigurationChangeSynchronisation
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectTypeId = objectTypeId,
            ConfigurationChangeActivityId = activity.Id == Guid.Empty ? null : activity.Id,
            SyncRuleId = syncRuleId,
            Reason = reason
        };

        try
        {
            await Application.Repository.ConnectedSystems.CreateConfigurationChangeSynchronisationAsync(change);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or TimeoutException)
        {
            var failure = new ConfigurationChangeSynchronisationNotRecordedException(
                connectedSystemName ?? $"Connected System {connectedSystemId}",
                objectTypeName ?? $"Object Type {objectTypeId}",
                ex);
            Log.Error(ex,
                "RecordConfigurationChangeAwaitingSynchronisationAsync: the configuration change was saved but could not be recorded as awaiting " +
                "synchronisation for Object Type {ObjectTypeId} of Connected System {ConnectedSystemId}; a Full Synchronisation of the system applies it",
                objectTypeId, connectedSystemId);
            await FailActivityOfUnrecordedChangeAsync(activity, failure);
            throw failure;
        }

        Log.Information(
            "RecordConfigurationChangeAwaitingSynchronisationAsync: Object Type {ObjectTypeId} of Connected System {ConnectedSystemId} is awaiting " +
            "synchronisation: {Reason}",
            objectTypeId, connectedSystemId, LogSanitiser.Sanitise(reason));
    }

    /// <summary>
    /// Fails an Activity whose change could not be recorded. A failure to write the failure must not replace the
    /// exception that explains it: the database that refused the row is likely to refuse this too, and the caller
    /// needs the first failure, with its remedy, more than the second.
    /// </summary>
    private async Task FailActivityOfUnrecordedChangeAsync(Activity activity, ConfigurationChangeSynchronisationNotRecordedException failure)
    {
        try
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, failure.Message);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or TimeoutException)
        {
            Log.Error(ex, "FailActivityOfUnrecordedChangeAsync: Activity {ActivityId} could not be marked as failed after its configuration change " +
                "could not be recorded as awaiting synchronisation", activity.Id);
        }
    }
}
