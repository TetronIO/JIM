// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using System.Security.Cryptography;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Turns one password change into one queued change per Connected System the identity has, or could have, an
/// account in (#1119): requirement 6's fan-out at the Metaverse.
/// <para>
/// Fan-out is at the Metaverse and nowhere else, which is the same rule the rest of JIM follows and matters more
/// here than anywhere: a password flowing directly from one Connected System to another would mean JIM holding a
/// credential on behalf of a system that never asked it to, with no record of where it came from or went.
/// </para>
/// <para>
/// Queueing is deliberately separate from delivering. A password change must not fail because a directory is
/// down, and the caller asking for it (an administrator at a screen, an API client) must not be held while every
/// target is written to in turn. This records the intent durably and returns; delivery happens on its own clock.
/// </para>
/// </summary>
public class PasswordSynchronisationServer
{
    private readonly ISyncRepository _syncRepo;
    private readonly Func<IConnectedSystemRepository> _connectedSystemRepo;
    private readonly Func<IPasswordProtectionService> _passwordProtection;
    private readonly Func<Activity, MetaverseObject?, Task> _createActivity;
    private readonly Func<Activity, Task> _completeActivity;

    /// <param name="passwordProtection">
    /// How to reach password protection, resolved when a change is queued rather than now. The hosts set
    /// <see cref="JimApplication.CredentialProtection"/> after constructing the facade, so anything captured here
    /// would capture the null that precedes it.
    /// </param>
    /// <param name="connectedSystemRepository">
    /// How to reach the Connected System repository, resolved when a change is queued rather than now, for the
    /// same reason as password protection: the facade's own repository properties are not populated until after
    /// this constructor has run, so anything read here would be the null that precedes them.
    /// </param>
    /// <param name="createActivity">Creates an Activity, attributed to the initiator passed with it.</param>
    /// <param name="completeActivity">Completes an Activity.</param>
    internal PasswordSynchronisationServer(
        ISyncRepository syncRepository,
        Func<IConnectedSystemRepository> connectedSystemRepository,
        Func<IPasswordProtectionService> passwordProtection,
        Func<Activity, MetaverseObject?, Task> createActivity,
        Func<Activity, Task> completeActivity)
    {
        _syncRepo = syncRepository;
        _connectedSystemRepo = connectedSystemRepository;
        _passwordProtection = passwordProtection;
        _createActivity = createActivity;
        _completeActivity = completeActivity;
    }

    /// <summary>
    /// Queues a password change for every Connected System enabled for Password Synchronisation, aimed at the
    /// identity's account there.
    /// </summary>
    /// <param name="metaverseObjectId">The identity whose password changed.</param>
    /// <param name="displayName">The identity's display name, for the Activity. Never the password.</param>
    /// <param name="password">
    /// The password. Encrypted before it is written and never logged, never returned, and never put on an
    /// Activity; it exists in cleartext here only for as long as this method runs.
    /// </param>
    /// <param name="expiryBehaviour">
    /// What should happen to the password once set. Carried per change rather than read from each system's
    /// configuration, because it belongs to the circumstance: an administrator setting a password on somebody's
    /// behalf may require a change at next sign-in, whereas a password the person chose themselves must not.
    /// </param>
    /// <param name="initiatedBy">The administrator making the change, for attribution.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<PasswordQueueResult> QueuePasswordChangeAsync(
        Guid metaverseObjectId,
        string displayName,
        string password,
        PasswordExpiryBehaviour expiryBehaviour,
        MetaverseObject? initiatedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A password is required.", nameof(password));

        var connectedSystems = _connectedSystemRepo();
        var targets = await connectedSystems.GetEnabledPasswordSynchronisationTargetsAsync();

        // The identity's accounts, read once and matched against every target, rather than a query per system.
        var accounts = await connectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId);

        var activity = new Activity
        {
            TargetName = displayName,
            TargetType = ActivityTargetType.PasswordSynchronisation,
            TargetOperationType = ActivityTargetOperationType.SetPassword,
            MetaverseObjectId = metaverseObjectId
        };
        await _createActivity(activity, initiatedBy);

        var outcomes = new List<PasswordQueueTargetOutcome>(targets.Count);
        var now = DateTime.UtcNow;
        var protection = _passwordProtection();
        var encrypted = protection.ProtectPassword(password)!;

        var changes = new List<PendingPasswordChange>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The identity's account of the type this system nominated. An identity can hold a Connected System
            // Object of another type in the same system, and a password belongs to the account.
            var account = accounts.SingleOrDefault(a =>
                a.ConnectedSystemId == target.ConnectedSystemId && a.TypeId == target.TargetObjectTypeId);

            changes.Add(new PendingPasswordChange
            {
                MetaverseObjectId = metaverseObjectId,
                ConnectedSystemId = target.ConnectedSystemId,
                // Null where the account does not exist yet, which is an ordinary state rather than a failure:
                // the change waits, bounded by its time to live, and delivery re-resolves the account each
                // attempt, so a password arriving before provisioning resolves itself (Resolved Decision 2).
                ConnectedSystemObjectId = account?.Id,
                EncryptedPassword = encrypted,
                ExpiryBehaviour = expiryBehaviour,
                CreatedAt = now,
                ExpiresAt = now + target.TimeToLive,
                ActivityId = activity.Id
            });

            outcomes.Add(new PasswordQueueTargetOutcome
            {
                ConnectedSystemId = target.ConnectedSystemId,
                ConnectedSystemName = target.ConnectedSystemName,
                ConnectedSystemObjectId = account?.Id
            });
        }

        // One batched write for the whole fan-out, per the non-functional requirement that a change across ten
        // systems is a single write rather than ten round trips.
        if (changes.Count > 0)
            await _syncRepo.QueuePasswordChangesAsync(changes);

        // Requirement 14: a change that reached nothing is still recorded, and says so. Silence here would let an
        // administrator believe a password propagated when nothing was even queued.
        activity.Message = outcomes.Count == 0
            ? "No Connected System is enabled for Password Synchronisation, so this password was not queued for delivery anywhere."
            : $"Password change queued for {outcomes.Count} Connected System{(outcomes.Count == 1 ? string.Empty : "s")}: " +
              string.Join(", ", outcomes.Select(o => o.ConnectedSystemName));

        await _completeActivity(activity);

        // Synchronisation Integrity: summary statistics at the end of every batch operation. The systems are
        // named because that is what an administrator needs; the password is not, and no part of it ever is.
        Log.Information(
            "QueuePasswordChangeAsync: Password change for Metaverse Object {MetaverseObjectId} queued for {TargetCount} Connected System(s): {Targets}",
            metaverseObjectId, outcomes.Count,
            outcomes.Count == 0 ? "none" : LogSanitiser.Sanitise(string.Join(", ", outcomes.Select(o => o.ConnectedSystemName))));

        return new PasswordQueueResult { ActivityId = activity.Id, Targets = outcomes };
    }

    /// <summary>
    /// How many queued password changes one pass will attempt against a Connected System.
    /// <para>
    /// A bound rather than a page size, matching the initial-password pass: a misconfigured target must not turn
    /// one pass into an unbounded run of failing round trips. What is left over is taken by the next pass, oldest
    /// first, so nothing starves.
    /// </para>
    /// </summary>
    public const int MaximumChangesPerPass = 1000;

    /// <summary>
    /// Delivers the password changes due on a Connected System, expiring anything that has outlived its window
    /// first.
    /// <para>
    /// Never throws for a delivery that did not work. Every outcome is classified and recorded against the change
    /// it belongs to, because a pass that threw would abandon the changes it had not reached and lose the outcomes
    /// of the ones it had.
    /// </para>
    /// </summary>
    /// <param name="connectedSystem">The Connected System to deliver to, loaded with its configuration.</param>
    /// <param name="connector">
    /// Its Connector. One that cannot set passwords leaves the queued changes exactly as they are rather than
    /// failing them: the capability may arrive with a Connector upgrade.
    /// </param>
    /// <param name="asOf">The instant the pass runs, for expiry and retry scheduling.</param>
    /// <param name="cancellationToken">
    /// Stops before the changes not yet reached. It cannot undo the deliveries already made, and their outcomes
    /// are still recorded: a password that landed has landed whatever the run did next.
    /// </param>
    public async Task<PasswordDeliveryRunResult> DeliverDuePasswordChangesAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var result = new PasswordDeliveryRunResult();

        // An unconfigured or disabled system delivers nothing, and keeps everything. Requirement 2: a disabled
        // system accumulates rather than discarding, so enabling it later has something to drain.
        var configuration = connectedSystem.PasswordSynchronisation;
        if (configuration is not { Enabled: true })
            return result;

        // Expiry first, so a change on its way out is not attempted, and its attempt count not inflated, on the
        // very pass that retires it.
        result.ExpiredCount = await _syncRepo.ExpirePasswordChangesAsync(connectedSystem.Id, asOf);

        var due = await _syncRepo.GetDuePasswordChangesAsync(connectedSystem.Id, asOf, MaximumChangesPerPass);
        if (due.Count == 0)
            return result;

        if (connector is not IConnectorPasswordManagement passwordConnector)
        {
            result.ConnectorCannotSetPasswords = true;
            Log.Warning(
                "DeliverDuePasswordChangesAsync: The Connector for Connected System {ConnectedSystemId} cannot set passwords. {Count} queued password change(s) are left outstanding.",
                connectedSystem.Id, due.Count);
            return result;
        }

        try
        {
            passwordConnector.OpenPasswordConnection(connectedSystem.SettingValues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported once for the pass rather than as a failure per change: the problem belongs to the
            // connection, and counting it against every change would inflate attempt counts that are supposed to
            // mean "distinct attempts at this password".
            result.CouldNotOpenPasswordConnection = true;
            Log.Error(ex,
                "DeliverDuePasswordChangesAsync: Could not open the password channel to Connected System {ConnectedSystemId}. {Count} queued password change(s) are left outstanding.",
                connectedSystem.Id, due.Count);
            return result;
        }

        var attempted = new List<PendingPasswordChange>();
        var delivered = new List<Guid>();

        try
        {
            foreach (var change in due)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var outcome = await DeliverOneAsync(passwordConnector, connectedSystem, change, configuration, asOf, cancellationToken);

                if (outcome)
                {
                    delivered.Add(change.Id);
                    result.DeliveredCount++;
                }
                else
                {
                    attempted.Add(change);
                    if (change.Status == PendingPasswordChangeStatus.Parked)
                        result.ParkedCount++;
                    else
                        result.RetryingCount++;
                }
            }
        }
        finally
        {
            passwordConnector.ClosePasswordConnection();

            // Persisted in the finally so a cancelled pass keeps what it achieved. Re-delivering a password
            // already set is harmless, but leaving a delivered change queued would send it again on every pass.
            if (attempted.Count > 0)
                await _syncRepo.RecordPasswordChangeAttemptsAsync(attempted);

            if (delivered.Count > 0)
                await _syncRepo.DeletePasswordChangesAsync(delivered);
        }

        // Synchronisation Integrity: summary statistics at the end of every batch operation.
        Log.Information(
            "DeliverDuePasswordChangesAsync: Connected System {ConnectedSystemId}: {Delivered} delivered, {Retrying} retrying, {Parked} parked, {Expired} expired.",
            connectedSystem.Id, result.DeliveredCount, result.RetryingCount, result.ParkedCount, result.ExpiredCount);

        return result;
    }

    /// <summary>
    /// Delivers one queued change, recording its outcome on the change. Returns true where the password was set
    /// and the change should be removed from the queue.
    /// </summary>
    private async Task<bool> DeliverOneAsync(
        IConnectorPasswordManagement passwordConnector,
        ConnectedSystem connectedSystem,
        PendingPasswordChange change,
        ConnectedSystemPasswordSynchronisation configuration,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        // The account is resolved on every attempt rather than trusted from when the change was queued, which is
        // what lets a change queued before provisioning deliver once the account appears, and what stops a
        // change being sent to an account that has since been deleted and replaced.
        var accounts = await _connectedSystemRepo().GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId);
        var account = accounts.SingleOrDefault(a =>
            a.ConnectedSystemId == connectedSystem.Id && a.TypeId == configuration.TargetObjectTypeId);

        if (account == null)
        {
            // Retry rather than park: the account may simply not have been provisioned yet, and the change's own
            // time to live is what bounds the wait (Resolved Decision 2).
            change.RecordAttempt(PasswordSetFailureReason.TargetObjectNotFound,
                "The identity has no account in this Connected System yet.", configuration, asOf);
            await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
            return false;
        }

        change.ConnectedSystemObjectId = account.Id;

        PasswordSetResult setResult;
        try
        {
            // The one point at which a synchronised password exists in cleartext, and only for this call.
            var password = _passwordProtection().UnprotectPassword(change.EncryptedPassword)!;

            setResult = await passwordConnector.SetPasswordAsync(account, password, new PasswordSetOptions
            {
                ExpiryBehaviour = change.ExpiryBehaviour,
                // Deliberately never enabling: that belongs to provisioning. A synchronised password reaches
                // accounts an administrator may have disabled on purpose, and re-enabling one would undo that
                // silently, as a side effect of somebody changing their password elsewhere.
                EnableAccount = null
            }, cancellationToken);
        }
        catch (CryptographicException ex)
        {
            // The key ring has been rotated or lost, so this change can never be decrypted. Parked rather than
            // retried: no number of attempts recovers a value nothing can read.
            Log.Error(ex,
                "DeliverDuePasswordChangesAsync: Could not decrypt a queued password for Connected System {ConnectedSystemId}. The encryption key may have been changed or lost.",
                connectedSystem.Id);
            change.RecordAttempt(PasswordSetFailureReason.ConfigurationFault,
                "JIM could not decrypt this password. The encryption key may have been changed or lost.",
                configuration, asOf);
            change.Status = PendingPasswordChangeStatus.Parked;
            change.NextRetryAt = null;
            await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A Connector that threw rather than classifying is treated as transient: the change is kept, and
            // one target's fault never stops the pass reaching the others.
            Log.Error(ex,
                "DeliverDuePasswordChangesAsync: The Connector threw setting a password on Connected System {ConnectedSystemId}.",
                connectedSystem.Id);
            change.RecordAttempt(PasswordSetFailureReason.Transient, ex.Message, configuration, asOf);
            await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
            return false;
        }

        if (!setResult.Success)
        {
            change.RecordAttempt(setResult.FailureReason, setResult.ErrorMessage, configuration, asOf);
            await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
            return false;
        }

        await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: true);
        return true;
    }

    /// <summary>
    /// Records what one Connected System did with a queued password change, as a child of the Activity for the
    /// change itself (requirement 23).
    /// <para>
    /// The child carries the outcome and the target's own words on a refusal, and never the password: the queue
    /// row is deleted on success, so this Activity is all that survives to say the password reached this system.
    /// </para>
    /// </summary>
    private async Task RecordDeliveryOutcomeActivityAsync(ConnectedSystem connectedSystem, PendingPasswordChange change, bool success)
    {
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.PasswordSynchronisation,
            TargetOperationType = ActivityTargetOperationType.SetPassword,
            // Set so the Activities list's existing per-Connected-System filter covers password events without
            // any new filter control (requirement 24).
            TargetContext = connectedSystem.Name,
            ParentActivityId = change.ActivityId,
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystemObjectId = change.ConnectedSystemObjectId,
            MetaverseObjectId = change.MetaverseObjectId,
            Message = success
                ? $"Password set on {connectedSystem.Name}."
                : DescribeFailure(connectedSystem, change)
        };

        await _createActivity(activity, null);
    }

    private static string DescribeFailure(ConnectedSystem connectedSystem, PendingPasswordChange change)
    {
        var disposition = change.Status == PendingPasswordChangeStatus.Parked
            ? "JIM has stopped trying; retry it once the cause is fixed"
            : "JIM will try again";

        var reason = string.IsNullOrWhiteSpace(change.TargetMessage)
            ? change.FailureReason?.ToString() ?? "an unknown reason"
            : change.TargetMessage;

        return $"Password not set on {connectedSystem.Name}: {reason}. {disposition}.";
    }

    /// <summary>
    /// Makes every parked password change on a Connected System due again, returning how many were released:
    /// requirement 3's drain when a system is enabled, and the same mechanic when its delivery settings change.
    /// </summary>
    public async Task<int> ReleaseForDeliveryAsync(int connectedSystemId)
    {
        var released = await _syncRepo.ReleasePasswordChangesForDeliveryAsync(connectedSystemId);

        if (released > 0)
            Log.Information(
                "ReleaseForDeliveryAsync: {Released} parked password change(s) on Connected System {ConnectedSystemId} are due again.",
                released, connectedSystemId);

        return released;
    }

    /// <summary>
    /// How much queued password work on each Connected System is waiting on a person. A system with nothing to
    /// report is absent from the dictionary rather than present with zeroes.
    /// </summary>
    public async Task<Dictionary<int, PasswordQueueAttention>> GetAttentionByConnectedSystemAsync(IReadOnlyCollection<int> connectedSystemIds)
    {
        return await _syncRepo.GetPasswordQueueAttentionAsync(connectedSystemIds);
    }
}
