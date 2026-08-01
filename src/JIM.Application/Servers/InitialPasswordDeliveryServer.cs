// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Gives the accounts JIM has provisioned the initial passwords they are owed (issue #1121).
/// <para>
/// Runs as its own pass after the export phase, over everything outstanding on the Connected System rather
/// than only what this run staged. That makes an ordinary export run the retry vehicle for an account whose
/// password could not be set last time, with no separate Run Profile to remember to schedule, and it means a
/// right granted or a directory brought back online is picked up by the next run that happens anyway.
/// </para>
/// <para>
/// <b>No password value leaves this class, or is written anywhere.</b> Each one is generated at the moment of
/// delivery, handed to the Connector, and dropped. What is recorded is that a password was owed, how many
/// times JIM has tried, and what the target said when it refused.
/// </para>
/// </summary>
public class InitialPasswordDeliveryServer
{
    /// <summary>
    /// The most accounts one pass will attempt.
    /// <para>
    /// A bound rather than a page size: a misconfigured target rejecting everything must not be able to turn
    /// an export run into an unbounded sequence of failing password attempts, each of which is a round trip.
    /// What is left over is attempted on the next run, and the oldest work goes first, so nothing starves.
    /// </para>
    /// </summary>
    public const int MaximumAccountsPerPass = 1000;

    private readonly ISyncRepository _syncRepo;
    private readonly InitialPasswordDeliveryService _deliveryService;

    internal InitialPasswordDeliveryServer(ISyncRepository syncRepository, IPasswordGeneratorService passwordGenerator)
    {
        _syncRepo = syncRepository;
        _deliveryService = new InitialPasswordDeliveryService(passwordGenerator);
    }

    /// <summary>
    /// Delivers the initial passwords outstanding on a Connected System.
    /// <para>
    /// Never throws for a delivery that did not work. Every outcome is classified and recorded against the
    /// account it belongs to, because this runs after an export whose objects were created successfully and
    /// must not be able to change that.
    /// </para>
    /// </summary>
    /// <param name="connectedSystem">The Connected System whose outstanding passwords to deliver.</param>
    /// <param name="connector">
    /// The Connector for that system. A Connector that cannot set passwords is reported as such and nothing is
    /// attempted; the outstanding records are left alone, since the capability may arrive with an upgrade.
    /// </param>
    public async Task<InitialPasswordRunResult> DeliverOutstandingAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(connector);

        var result = new InitialPasswordRunResult();

        var outstanding = await _syncRepo.GetOutstandingInitialPasswordsAsync(connectedSystem.Id, MaximumAccountsPerPass);
        if (outstanding.Count == 0)
            return result;

        if (connector is not IConnectorPasswordManagement passwordConnector)
        {
            // Left outstanding on purpose. The accounts genuinely are owed passwords, and a Connector that
            // gained the capability later would find the work waiting for it.
            result.ConnectorCannotSetPasswords = true;
            Log.Warning("DeliverOutstandingAsync: {Count} accounts on {SystemName} are owed an initial password, but its Connector cannot set passwords",
                outstanding.Count, LogSanitiser.Sanitise(connectedSystem.Name));
            return result;
        }

        // Configuration is re-read on every pass, never cached from when the work was staged, so that an
        // administrator correcting settings that a target rejected is picked up by the next attempt with no
        // invalidation machinery. This is the same reason the password itself is generated at delivery time.
        var configurations = await _syncRepo.GetInitialPasswordConfigurationsAsync(
            outstanding.Where(p => p.SyncRuleId.HasValue).Select(p => p.SyncRuleId!.Value).Distinct().ToList());
        var discoveredPolicy = await _syncRepo.GetDiscoveredPasswordPolicyAsync(connectedSystem.Id);

        var delivered = new List<Guid>();
        var attempts = new List<PendingInitialPassword>();

        try
        {
            passwordConnector.OpenPasswordConnection(connectedSystem.SettingValues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One connection problem is one thing for an administrator to fix, not one per account, so this is
            // reported on its own rather than as N identical failures with N incremented attempt counts.
            result.CouldNotOpenPasswordConnection = true;
            result.PasswordConnectionErrorMessage = ex.Message;
            Log.Error(ex, "DeliverOutstandingAsync: Could not open the password connection to {SystemName}; {Count} accounts are still owed an initial password",
                LogSanitiser.Sanitise(connectedSystem.Name), outstanding.Count);
            return result;
        }

        try
        {
            foreach (var pending in outstanding)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var configuration = pending.SyncRuleId.HasValue && configurations.TryGetValue(pending.SyncRuleId.Value, out var found)
                    ? found
                    : null;

                var outcome = await _deliveryService.DeliverAsync(
                    passwordConnector, pending.ConnectedSystemObject, configuration, discoveredPolicy, cancellationToken);

                Record(pending, outcome, result, delivered, attempts);
            }
        }
        finally
        {
            passwordConnector.ClosePasswordConnection();

            // In the finally so that cancelling a run still persists what was achieved before it stopped.
            // Re-attempting an account JIM has already given a password to would reset a password somebody may
            // already be using.
            if (delivered.Count > 0)
                await _syncRepo.DeleteInitialPasswordsAsync(delivered);
            if (attempts.Count > 0)
                await _syncRepo.RecordInitialPasswordAttemptsAsync(attempts);
        }

        // Synchronisation Integrity: summary statistics at the end of every batch operation.
        Log.Information("DeliverOutstandingAsync: Initial passwords for {SystemName}: {Attempted} attempted, {Delivered} delivered, " +
            "{Retrying} to retry, {Parked} parked for an administrator, {NoLongerApplicable} no longer applicable",
            LogSanitiser.Sanitise(connectedSystem.Name), result.AttemptedCount, result.DeliveredCount,
            result.RetryingCount, result.ParkedCount, result.NoLongerApplicableCount);

        return result;
    }

    /// <summary>
    /// Turns one account's outcome into what happens to its record, and counts it.
    /// </summary>
    private static void Record(
        PendingInitialPassword pending,
        InitialPasswordDeliveryResult outcome,
        InitialPasswordRunResult result,
        List<Guid> delivered,
        List<PendingInitialPassword> attempts)
    {
        switch (outcome.Outcome)
        {
            case InitialPasswordDeliveryOutcome.Delivered:
                result.AttemptedCount++;
                result.DeliveredCount++;
                delivered.Add(pending.Id);
                if (outcome.Message != null)
                    Log.Warning("DeliverOutstandingAsync: Connected System Object {CsoId} was given its initial password, with a caveat: {Caveat}",
                        pending.ConnectedSystemObjectId, LogSanitiser.Sanitise(outcome.Message));
                break;

            case InitialPasswordDeliveryOutcome.NotApplicable:
                // The rule no longer asks for an initial password, or has gone. There is nothing to deliver
                // and nothing an administrator needs to repair, so the work list stops carrying it.
                result.NoLongerApplicableCount++;
                delivered.Add(pending.Id);
                break;

            case InitialPasswordDeliveryOutcome.Retry:
                result.AttemptedCount++;
                result.RetryingCount++;
                attempts.Add(StampAttempt(pending, PendingInitialPasswordStatus.Pending, outcome));
                break;

            case InitialPasswordDeliveryOutcome.Parked:
                result.AttemptedCount++;
                result.ParkedCount++;
                attempts.Add(StampAttempt(pending, PendingInitialPasswordStatus.Parked, outcome));
                Log.Warning("DeliverOutstandingAsync: Connected System Object {CsoId} could not be given an initial password and has been parked for an administrator: {Reason}",
                    pending.ConnectedSystemObjectId, LogSanitiser.Sanitise(outcome.Message));
                break;

            default:
                throw new NotSupportedException($"Unhandled initial password delivery outcome '{outcome.Outcome}'.");
        }
    }

    /// <summary>
    /// Records what this attempt found on the outstanding record. The target's own words are kept verbatim:
    /// why a directory refuses a password is a property of that directory's policy, and is the single most
    /// useful thing an administrator can be shown.
    /// </summary>
    private static PendingInitialPassword StampAttempt(
        PendingInitialPassword pending,
        PendingInitialPasswordStatus status,
        InitialPasswordDeliveryResult outcome)
    {
        pending.Status = status;
        pending.FailureReason = outcome.FailureReason;
        pending.TargetMessage = outcome.Message;
        pending.AttemptCount++;
        pending.LastAttemptedAt = DateTime.UtcNow;
        return pending;
    }
}
