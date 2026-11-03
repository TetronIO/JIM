// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Exchange;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Delivers the Exchange queue (#2021, Phase 5): the background lane that takes the recipient state an export left
/// for an account and has Exchange apply it, on its own clock, so an export never waits on Exchange or fails because
/// of it (PRD requirements 9 to 11, 26 and 27).
/// <para>
/// A lane runs per Connected System. It claims the due changes under a lease, opens one Exchange session through the
/// Connector's <see cref="IConnectorExchangeRecipients"/> capability, and converges each change in turn, recording the
/// outcome on the queued change and, when it says something new, as a child of the change's own Activity:
/// </para>
/// <list type="bullet">
/// <item><b>Delivered:</b> the change is removed; the confirming import then reads Exchange's result back and releases
/// the hold on the export's changes.</item>
/// <item><b>Retrying or Parked:</b> by how it failed (<see cref="PendingExchangeChange.RecordFailure"/>).</item>
/// <item><b>Withdrawn:</b> the account, or the Synchronisation Rule that declared the change, was removed first, or the
/// account's declaration has moved on since the change was queued. Exchange is not asked; the change is removed.</item>
/// </list>
/// <para>
/// Child Activities are written only when an outcome says something new: a delivery, a withdrawal, the first failure,
/// a failure of a different kind or message, and a park (decided 2026-11-03). Repeating the same failure updates only
/// the queued change, which always carries the latest attempt, so an Exchange outage over a large joiner batch records
/// one Activity per change rather than one per attempt.
/// </para>
/// </summary>
public class ExchangeDeliveryServer
{
    /// <summary>How many changes one lane claims at a time, so a claim never outlives the attempts it covers by much.</summary>
    public const int ClaimBatchSize = 100;

    /// <summary>
    /// The most changes one lane converges before it hands back to the scheduler, so a long queue on one system
    /// cannot starve the others. The rest are due at once and the scheduler comes straight back for them.
    /// </summary>
    public const int MaximumChangesPerPass = 1000;

    /// <summary>
    /// How many Exchange sessions a lane opens on one Connected System. One serves the whole batch (the PRD's
    /// throughput requirement); PRD decision 4 sets the bound by measurement, and it is raised once spike Phase 3 and
    /// the integration tests have measured Exchange's per-command cost.
    /// </summary>
    public const int MaximumSessionsPerConnectedSystem = 1;

    private readonly ISyncRepository _syncRepo;
    private readonly Func<IConnectedSystemRepository> _connectedSystemRepo;
    private readonly Func<IActivityRepository> _activityRepo;
    private readonly Func<ConnectedSystem, IConnector> _createConnector;
    private readonly Func<Activity, Task> _createSystemActivity;
    private readonly Func<Activity, Task> _completeActivity;
    private readonly Func<Activity, string, Task> _completeActivityWithError;
    private readonly int _claimBatchSize;
    private readonly int _maximumChangesPerPass;

    /// <param name="syncRepository">The queue.</param>
    /// <param name="connectedSystemRepository">Loads the Connected System a lane delivers to; resolved per pass.</param>
    /// <param name="activityRepository">Reads what was recorded under each change, for where a change stands.</param>
    /// <param name="createConnector">Builds the Connector, configured as every other path builds it.</param>
    /// <param name="createSystemActivity">Creates an outcome Activity attributed to JIM itself: delivery runs unattended,
    /// long after whoever exported the account has gone.</param>
    /// <param name="completeActivity">Completes an outcome Activity.</param>
    /// <param name="completeActivityWithError">Completes a failure's outcome Activity with its error.</param>
    /// <param name="claimBatchSize">Overrides <see cref="ClaimBatchSize"/>, for tests.</param>
    /// <param name="maximumChangesPerPass">Overrides <see cref="MaximumChangesPerPass"/>, for tests.</param>
    internal ExchangeDeliveryServer(
        ISyncRepository syncRepository,
        Func<IConnectedSystemRepository> connectedSystemRepository,
        Func<IActivityRepository> activityRepository,
        Func<ConnectedSystem, IConnector> createConnector,
        Func<Activity, Task> createSystemActivity,
        Func<Activity, Task> completeActivity,
        Func<Activity, string, Task> completeActivityWithError,
        int claimBatchSize = ClaimBatchSize,
        int maximumChangesPerPass = MaximumChangesPerPass)
    {
        _syncRepo = syncRepository;
        _connectedSystemRepo = connectedSystemRepository;
        _activityRepo = activityRepository;
        _createConnector = createConnector;
        _createSystemActivity = createSystemActivity;
        _completeActivity = completeActivity;
        _completeActivityWithError = completeActivityWithError;
        _claimBatchSize = claimBatchSize;
        _maximumChangesPerPass = maximumChangesPerPass;
    }

    /// <summary>The Connected Systems with Exchange changes a lane would attempt now.</summary>
    public Task<List<int>> GetConnectedSystemIdsWithWorkDueAsync(DateTime asOf) =>
        _syncRepo.GetConnectedSystemIdsWithDueExchangeChangesAsync(asOf, PendingExchangeChange.ClaimLease);

    /// <summary>What the Exchange queue has ahead, for the delivery service's wait and heartbeat.</summary>
    public Task<DeliveryQueueOutlook> GetDeliveryOutlookAsync(DateTime asOf) =>
        _syncRepo.GetExchangeQueueDeliveryOutlookAsync(asOf, PendingExchangeChange.ClaimLease);

    /// <summary>
    /// Removes Exchange changes left Parked or Cancelled since before <paramref name="olderThan"/>, at most
    /// <paramref name="maxRecords"/>, for history retention (plan D10).
    /// </summary>
    public Task<int> DeleteExpiredQueueRecordsAsync(DateTime olderThan, int maxRecords) =>
        _syncRepo.DeleteTerminalExchangeChangesAsync(olderThan, maxRecords);

    /// <summary>
    /// Where each of <paramref name="changeActivities"/> stands now (#2021, Phase 6), keyed by Activity id: the
    /// follow-ups of an export's items, an account's outstanding change, a change's own page. Activities that are not
    /// a queued Exchange change's own (a password follow-up, or a notice recorded under a change) are left out.
    /// <para>
    /// Two reads whatever the number asked about: the queue rows still carrying the changes, and the Activities
    /// recorded under them.
    /// </para>
    /// </summary>
    public async Task<Dictionary<Guid, ExchangeChangeOutcome>> GetChangeOutcomesAsync(IReadOnlyCollection<Activity> changeActivities)
    {
        var changes = changeActivities
            .Where(a => a is { TargetType: ActivityTargetType.ExchangeRecipient, ParentActivityId: null })
            .DistinctBy(a => a.Id)
            .ToList();
        if (changes.Count == 0)
            return [];

        var ids = changes.Select(a => a.Id).ToList();
        // One row per change at most: IX_PendingExchangeChanges_ActivityId_Unique.
        var rows = (await _syncRepo.GetExchangeChangesByActivityAsync(ids)).ToDictionary(r => r.ActivityId);
        var events = (await _activityRepo().GetExchangeChangeEventsAsync(ids)).ToLookup(e => e.ChangeActivityId);

        return changes.ToDictionary(a => a.Id, a => ExchangeChangeOutcome.Describe(a, rows.GetValueOrDefault(a.Id), events[a.Id]));
    }

    /// <summary>
    /// Where one queued Exchange change stands now, or null where <paramref name="changeActivityId"/> is not a queued
    /// Exchange change's own Activity.
    /// </summary>
    public async Task<ExchangeChangeOutcome?> GetChangeOutcomeAsync(Guid changeActivityId)
    {
        var activity = await _activityRepo().GetActivityAsync(changeActivityId);
        if (activity == null)
            return null;

        var outcomes = await GetChangeOutcomesAsync([activity]);
        return outcomes.GetValueOrDefault(changeActivityId);
    }

    /// <summary>
    /// Runs one lane on <paramref name="connectedSystemId"/>: claims what is due, converges it over one Exchange session,
    /// and records every outcome. Outcomes already recorded stay recorded when the lane is cancelled; what it had not
    /// reached is given back unattempted.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System to deliver to.</param>
    /// <param name="claimedBy">The service instance delivering, stamped on every change it claims.</param>
    /// <param name="asOf">The moment the lane runs as of: what is due, and the time of each attempt.</param>
    /// <param name="cancellationToken">Stops the lane between changes.</param>
    public async Task<ExchangeDeliveryRunResult> DeliverDueAsync(int connectedSystemId, string claimedBy, DateTime asOf, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

        var result = new ExchangeDeliveryRunResult();
        var connectedSystem = await _connectedSystemRepo().GetConnectedSystemForExchangeDeliveryAsync(connectedSystemId);
        if (connectedSystem == null)
        {
            // Deleted between a change being queued and this lane reaching it; its queue rows go with it.
            Log.Debug("ExchangeDeliveryServer: Connected System {ConnectedSystemId} no longer exists; nothing to deliver.", connectedSystemId);
            return result;
        }

        result.ConnectedSystemName = connectedSystem.Name;

        var claimed = await _syncRepo.ClaimDueExchangeChangesAsync(connectedSystem.Id, claimedBy, asOf, PendingExchangeChange.ClaimLease,
            Math.Min(_claimBatchSize, _maximumChangesPerPass));
        if (claimed.Count == 0)
            return result;

        IConnector? connector = null;
        try
        {
            connector = _createConnector(connectedSystem);
            if (connector is not IConnectorExchangeRecipients exchange)
            {
                // Nothing was tried, so nothing is counted against the changes: they are due again at once, and the
                // scheduler holds the system off for a poll so the same lane does not run back to back.
                result.ConnectorCannotManageExchangeRecipients = true;
                result.ReleasedCount += await _syncRepo.ReleaseExchangeChangeClaimsAsync(claimed.Select(c => c.Id), claimedBy);
                return result;
            }

            await DeliverOverSessionAsync(connectedSystem, exchange, claimed, claimedBy, asOf, result, cancellationToken);
        }
        finally
        {
            // IConnector carries no disposal contract, but concrete Connectors hold connections.
            (connector as IDisposable)?.Dispose();

            Log.Information(
                "ExchangeDeliveryServer: Connected System {ConnectedSystem}: {Delivered} delivered ({AlreadyInState} already in their declared state), " +
                "{Retrying} retrying, {Parked} parked, {Withdrawn} withdrawn, {Released} released unattempted. Exchange session: {KerberosLogins} Kerberos " +
                "login(s), {Connections} connection(s), {Commands} command(s).{Problem:l}",
                LogSanitiser.Sanitise(connectedSystem.Name), result.DeliveredCount, result.AlreadyInStateCount, result.RetryingCount, result.ParkedCount,
                result.WithdrawnCount, result.ReleasedCount, result.SessionStatistics.KerberosLogins, result.SessionStatistics.Connections,
                result.SessionStatistics.Commands, DescribeProblem(result));
        }

        return result;
    }

    private async Task DeliverOverSessionAsync(ConnectedSystem connectedSystem, IConnectorExchangeRecipients exchange, List<PendingExchangeChange> claimed,
        string claimedBy, DateTime asOf, ExchangeDeliveryRunResult result, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await exchange.OpenExchangeSessionAsync(connectedSystem.SettingValues, cancellationToken);
            }
            catch (ExchangeSessionException ex)
            {
                // Exchange down, or the sign-in refused: every claimed change is owed the reason, so it shows against
                // the person as Retrying (PRD Scenario 5) rather than waiting in silence, and backs off on its own clock.
                result.SessionFailure = ex.Message;
                Log.Warning("ExchangeDeliveryServer: Could not open an Exchange session for {ConnectedSystem} ({FailureKind}): {Reason}",
                    LogSanitiser.Sanitise(connectedSystem.Name), ex.FailureKind, LogSanitiser.Sanitise(ex.Message));
                await RecordSessionFailureAsync(connectedSystem, claimed, ex, claimedBy, asOf, result);
                return;
            }

            var attemptedInTotal = 0;
            var batch = claimed;
            while (true)
            {
                attemptedInTotal += batch.Count;
                await DeliverBatchAsync(connectedSystem, exchange, batch, claimedBy, asOf, result, cancellationToken);

                if (cancellationToken.IsCancellationRequested || batch.Count < _claimBatchSize || attemptedInTotal >= _maximumChangesPerPass)
                    break;

                batch = await _syncRepo.ClaimDueExchangeChangesAsync(connectedSystem.Id, claimedBy, asOf, PendingExchangeChange.ClaimLease,
                    Math.Min(_claimBatchSize, _maximumChangesPerPass - attemptedInTotal));
                if (batch.Count == 0)
                    break;
            }
        }
        finally
        {
            result.SessionStatistics = exchange.GetSessionStatistics();
            exchange.CloseExchangeSession();
        }
    }

    private async Task DeliverBatchAsync(ConnectedSystem connectedSystem, IConnectorExchangeRecipients exchange, List<PendingExchangeChange> batch,
        string claimedBy, DateTime asOf, ExchangeDeliveryRunResult result, CancellationToken cancellationToken)
    {
        var objects = (await _syncRepo.GetExchangeDeliveryObjectsAsync(
                batch.Where(c => c.ConnectedSystemObjectId.HasValue).Select(c => c.ConnectedSystemObjectId!.Value).ToList()))
            .ToDictionary(o => o.ConnectedSystemObjectId);

        var attempted = new List<PendingExchangeChange>();
        var removed = new List<Guid>();
        var unattempted = new List<Guid>();
        try
        {
            foreach (var change in batch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    unattempted.Add(change.Id);
                    continue;
                }

                var withdrawal = WithdrawalReason(change, objects);
                if (withdrawal != null)
                {
                    removed.Add(change.Id);
                    result.WithdrawnCount++;
                    await RecordOutcomeAsync(connectedSystem, change, $"Withdrawn: {withdrawal} Exchange was not asked to make it.", error: null,
                        ActivityTargetOperationType.WithdrawExchangeRecipient);
                    continue;
                }

                var target = Target(change, objects[change.ConnectedSystemObjectId!.Value]);
                ExchangeConvergeResult converged;
                try
                {
                    converged = await exchange.ConvergeAsync(target, ExchangeDeclaredState.Of(change), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    unattempted.Add(change.Id);
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The capability's contract is to return a failure rather than throw one; this is a Connector fault
                    // (a dropped session, say). Recorded against this change as a failure that may clear, so it is
                    // retried and reported, and the lane carries on with the rest rather than stranding them.
                    Log.Error(ex, "ExchangeDeliveryServer: Converging {Target} on {ConnectedSystem} failed unexpectedly; recorded as a failure to retry.",
                        LogSanitiser.Sanitise(target.ToString()), LogSanitiser.Sanitise(connectedSystem.Name));
                    converged = ExchangeConvergeResult.Failure(ExchangeChangeFailureKind.Unreachable, ex.Message);
                }

                if (converged.Delivered)
                {
                    removed.Add(change.Id);
                    result.DeliveredCount++;
                    if (!converged.ChangedAnything)
                        result.AlreadyInStateCount++;
                    await RecordOutcomeAsync(connectedSystem, change, converged.ChangedAnything
                        ? $"Exchange recipient set: {change.RecipientType}."
                        : $"Exchange already had the declared recipient ({change.RecipientType}); nothing needed changing.", error: null);
                    continue;
                }

                attempted.Add(change);
                await RecordFailureAsync(connectedSystem, change, converged.FailureKind ?? ExchangeChangeFailureKind.Unreachable, converged.Message, asOf, result);
            }
        }
        finally
        {
            // Persisted whatever happened above, cancellation included: what was attempted stays attempted, and what
            // was not reached goes back unattempted rather than waiting out the lease.
            await _syncRepo.RecordExchangeChangeAttemptsAsync(attempted, claimedBy);
            await _syncRepo.DeleteExchangeChangesAsync(removed, claimedBy);
            result.ReleasedCount += await _syncRepo.ReleaseExchangeChangeClaimsAsync(unattempted, claimedBy);
        }
    }

    /// <summary>
    /// Why a claimed change is no longer owed, or null where it still is (requirement 11's Withdrawn). The account and
    /// the declaring Synchronisation Rule are cleared by the database when removed. The declaration is read afresh:
    /// export evaluation withdraws a change awaiting Exchange when the declaration goes back to what Exchange last
    /// stamped, and an import can move the account on, so a change queued earlier can describe a state nobody declares
    /// any more.
    /// </summary>
    private static string? WithdrawalReason(PendingExchangeChange change, IReadOnlyDictionary<Guid, ExchangeDeliveryObject> objects)
    {
        if (change.ConnectedSystemObjectId is not { } objectId || !objects.TryGetValue(objectId, out var account))
            return "the account was removed before Exchange acted.";

        if (change.SyncRuleId == null)
            return "the Synchronisation Rule that declared it was removed before Exchange acted.";

        if (!change.Declares(ExchangeDeclaredState.Of(account.ImportedValues, account.HeldChanges)))
            return "the account's declared recipient state changed before Exchange acted; any change still owed is queued by its next export.";

        return null;
    }

    private static ExchangeRecipientTarget Target(PendingExchangeChange change, ExchangeDeliveryObject account) => new()
    {
        ConnectedSystemObjectId = account.ConnectedSystemObjectId,
        ObjectGuid = account.ObjectGuid,
        DistinguishedName = account.DistinguishedName,
        ObjectType = account.ObjectType,
        DomainController = change.DomainController
    };

    private async Task RecordSessionFailureAsync(ConnectedSystem connectedSystem, List<PendingExchangeChange> claimed, ExchangeSessionException failure,
        string claimedBy, DateTime asOf, ExchangeDeliveryRunResult result)
    {
        foreach (var change in claimed)
            await RecordFailureAsync(connectedSystem, change, failure.FailureKind, failure.Message, asOf, result);

        await _syncRepo.RecordExchangeChangeAttemptsAsync(claimed, claimedBy);
    }

    /// <summary>
    /// Records a failed attempt on <paramref name="change"/>, and an Activity for it where it says something new: the
    /// first failure, a different kind or message, or the failure that parks the change.
    /// </summary>
    private async Task RecordFailureAsync(ConnectedSystem connectedSystem, PendingExchangeChange change, ExchangeChangeFailureKind kind, string? message,
        DateTime asOf, ExchangeDeliveryRunResult result)
    {
        var repeats = change.RepeatsLastFailure(kind, message);
        change.RecordFailure(kind, message, asOf);

        var parked = change.Status == PendingExchangeChangeStatus.Parked;
        if (parked)
            result.ParkedCount++;
        else
            result.RetryingCount++;

        if (repeats && !parked)
            return;

        var error = $"Exchange recipient not set ({Describe(kind)}): {message ?? "Exchange gave no reason"}. " +
                    (parked ? "JIM has stopped trying; retry it once the cause is fixed." : "JIM will try again.");
        await RecordOutcomeAsync(connectedSystem, change, error, error);
    }

    /// <summary>
    /// A child of the change's own Activity: System-attributed, completed as what it was, with the account and the
    /// Connected System set so the follow-up shows against both. A delivery or a failed attempt is
    /// <see cref="ActivityTargetOperationType.SetExchangeRecipient"/>, told apart by its status; a withdrawal has an
    /// operation of its own, so what became of the change can be read without parsing its words.
    /// </summary>
    private async Task RecordOutcomeAsync(ConnectedSystem connectedSystem, PendingExchangeChange change, string message, string? error,
        ActivityTargetOperationType operation = ActivityTargetOperationType.SetExchangeRecipient)
    {
        var activity = new Activity
        {
            TargetType = ActivityTargetType.ExchangeRecipient,
            TargetOperationType = operation,
            TargetName = connectedSystem.Name,
            TargetContext = change.RecipientType,
            ParentActivityId = change.ActivityId,
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystemObjectId = change.ConnectedSystemObjectId,
            MetaverseObjectId = change.MetaverseObjectId,
            Message = message
        };

        await _createSystemActivity(activity);
        if (error == null)
            await _completeActivity(activity);
        else
            await _completeActivityWithError(activity, error);
    }

    private static string Describe(ExchangeChangeFailureKind kind) => kind switch
    {
        ExchangeChangeFailureKind.ObjectNotFound => "Exchange could not find the account",
        ExchangeChangeFailureKind.Refused => "Exchange refused the change",
        ExchangeChangeFailureKind.InsufficientPermissions => "JIM's account lacks the Exchange permissions",
        ExchangeChangeFailureKind.AuthenticationFailed => "signing in to Exchange failed",
        ExchangeChangeFailureKind.Unreachable => "Exchange was unreachable or timed out",
        _ => kind.ToString()
    };

    private static string DescribeProblem(ExchangeDeliveryRunResult result)
    {
        if (result.ConnectorCannotManageExchangeRecipients)
            return " The Connector cannot manage Exchange recipients.";
        return result.SessionFailure == null ? string.Empty : $" The Exchange session could not be opened: {LogSanitiser.Sanitise(result.SessionFailure)}";
    }
}
