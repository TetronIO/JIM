// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using System.Security.Cryptography;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
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
    private readonly Func<IActivityRepository> _activityRepo;
    private readonly Func<IPasswordProtectionService> _passwordProtection;
    private readonly Func<ConnectedSystem, IConnector> _createConnector;
    private readonly Func<Activity, MetaverseObject?, ApiKey?, Task> _createActivity;
    private readonly Func<Activity, Task> _completeActivity;
    private readonly Func<Activity, string, Task> _completeActivityWithError;
    private readonly Func<int?, Task> _requestDelivery;

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
    /// <param name="activityRepository">
    /// How to reach the Activity repository, resolved on use rather than now, for the same reason as the
    /// Connected System repository above. Read-only here: this server records Activities through the Activity
    /// server's callbacks, and reaches the repository only to read an identity's password history back.
    /// </param>
    /// <param name="createConnector">
    /// Resolves a Connected System's Connector, already configured with credential protection and certificate
    /// validation. A delegate rather than the factory itself, so this server never needs to know how a Connector
    /// is built or what has to be injected into it before it can decrypt a bind credential.
    /// </param>
    /// <param name="createActivity">
    /// Creates an Activity, attributed to whichever initiator is passed with it. Exactly one of the two is set:
    /// an administrator at a screen, or the API key an automation authenticated with. An Activity with neither
    /// is refused by the Activity server, and rightly: a password change nobody can be shown to have made is not
    /// an audit record.
    /// </param>
    /// <param name="requestDelivery">
    /// Asks for a delivery pass over the given Connected System, or over every system with work due where null.
    /// Queueing and delivering stay separate (a password change must not wait on a directory), so this is how the
    /// queue tells the worker there is something to do rather than leaving it to the next housekeeping tick.
    /// </param>
    /// <param name="completeActivity">Completes an Activity.</param>
    /// <param name="completeActivityWithError">
    /// Completes an Activity as a failure, carrying the reason. A separate delegate because a target refusing a
    /// password is an operational outcome rather than a thrown exception: nothing here has an exception to pass,
    /// and the outcome still has to be recorded as a failure rather than described in prose on a completed one.
    /// </param>
    internal PasswordSynchronisationServer(
        ISyncRepository syncRepository,
        Func<IConnectedSystemRepository> connectedSystemRepository,
        Func<IActivityRepository> activityRepository,
        Func<IPasswordProtectionService> passwordProtection,
        Func<ConnectedSystem, IConnector> createConnector,
        Func<Activity, MetaverseObject?, ApiKey?, Task> createActivity,
        Func<Activity, Task> completeActivity,
        Func<Activity, string, Task> completeActivityWithError,
        Func<int?, Task> requestDelivery)
    {
        _syncRepo = syncRepository;
        _connectedSystemRepo = connectedSystemRepository;
        _activityRepo = activityRepository;
        _passwordProtection = passwordProtection;
        _createConnector = createConnector;
        _createActivity = createActivity;
        _completeActivity = completeActivity;
        _completeActivityWithError = completeActivityWithError;
        _requestDelivery = requestDelivery;
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
        return await QueuePasswordChangeAsync(metaverseObjectId, displayName, password, expiryBehaviour,
            initiatedBy, initiatedByApiKey: null, cancellationToken);
    }

    /// <summary>
    /// Queues a password change initiated by an API key rather than by a person (#1119).
    /// <para>
    /// Automation is the expected caller for a synchronised password change: a self-service portal or a service
    /// desk tool tells JIM that somebody's password has changed. An Activity must still name who did it, and an
    /// API key is a security principal exactly as an administrator is.
    /// </para>
    /// </summary>
    public async Task<PasswordQueueResult> QueuePasswordChangeAsync(
        Guid metaverseObjectId,
        string displayName,
        string password,
        PasswordExpiryBehaviour expiryBehaviour,
        ApiKey initiatedByApiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initiatedByApiKey);

        return await QueuePasswordChangeAsync(metaverseObjectId, displayName, password, expiryBehaviour,
            initiatedBy: null, initiatedByApiKey, cancellationToken);
    }

    private async Task<PasswordQueueResult> QueuePasswordChangeAsync(
        Guid metaverseObjectId,
        string displayName,
        string password,
        PasswordExpiryBehaviour expiryBehaviour,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey,
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
        await _createActivity(activity, initiatedBy, initiatedByApiKey);

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

        // Unscoped, because fan-out reaches every enabled system at once and the pass resolves which of them
        // actually have work due. Nothing queued means nothing to ask for.
        if (changes.Count > 0)
            await _requestDelivery(null);

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

        // The administrator's declaration that passwords must not leave JIM in the clear for this system,
        // applied to the channel the Connector actually opened. The rule is shared with the other two paths that
        // write a password here, so all three refuse on identical grounds. Nothing is attempted and no attempt is
        // counted: the changes stay Pending and due, so no release is needed when the setting is turned off
        // again; the worker's idle housekeeping finds them within the minute and raises a pass.
        if (PasswordChannelSecurity.RefusesChannel(connectedSystem, passwordConnector))
        {
            passwordConnector.ClosePasswordConnection();
            result.PasswordChannelNotSecure = true;
            Log.Error(
                "DeliverDuePasswordChangesAsync: Connected System {ConnectedSystemId} requires a secure transport for passwords, and the Connector's password channel is not encrypted. {Count} queued password change(s) are left outstanding.",
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
        var failure = success ? null : DescribeFailure(connectedSystem, change);

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
                : failure
        };

        await _createActivity(activity, null, null);

        // Completed, and completed as what it was. Creating an Activity sets it InProgress, so an outcome that is
        // never completed sits in the Activities list looking like work still under way; and a refusal recorded
        // only as prose in the Message is invisible to everything that counts, filters or alerts on outcomes,
        // which is most of what an audit record is for (requirement 23).
        if (success)
            await _completeActivity(activity);
        else
            await _completeActivityWithError(activity, failure!);
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
    /// How many Connected Systems are configured and enabled to receive synchronised passwords (#1119).
    /// <para>
    /// Read by the portal so the Synchronise Password action is offered or not, rather than appearing and then
    /// turning out to reach nothing once somebody has typed a password into it.
    /// </para>
    /// </summary>
    public async Task<int> GetEnabledTargetCountAsync()
    {
        var targets = await _connectedSystemRepo().GetEnabledPasswordSynchronisationTargetsAsync();
        return targets.Count;
    }

    /// <summary>
    /// Whether any Connected System has queued password work due as of the given moment (#1119).
    /// <para>
    /// Asked by the worker's idle housekeeping, which is the only trigger that catches a retry: a change that
    /// failed once comes due minutes later without anything else happening in the system.
    /// </para>
    /// </summary>
    public async Task<bool> HasWorkDueAsync(DateTime asOf)
    {
        var connectedSystemIds = await _syncRepo.GetConnectedSystemIdsWithDuePasswordChangesAsync(asOf);
        return connectedSystemIds.Count > 0;
    }

    /// <summary>
    /// Runs a delivery pass over the Connected Systems with password work due, resolving each system's Connector
    /// as it goes. This is what the worker's Password Delivery task calls.
    /// <para>
    /// A system that cannot be delivered to is recorded and stepped over rather than thrown from. A pass that
    /// threw on the first unreachable directory would leave every system behind it in the list undelivered, which
    /// is exactly the failure mode Password Synchronisation exists to avoid: somebody's password differing
    /// between systems because one of them happened to be down.
    /// </para>
    /// </summary>
    /// <param name="connectedSystemId">
    /// The Connected System to deliver to, or null to visit every system with work due. Named where the trigger
    /// knows which system it is, so a targeted delivery does not sweep systems with nothing to do.
    /// </param>
    /// <param name="asOf">The moment the pass is running as of; what is due and what has expired are read from it.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the pass.</param>
    public async Task<PasswordDeliveryPassResult> DeliverDueAsync(int? connectedSystemId, DateTime asOf, CancellationToken cancellationToken)
    {
        var result = new PasswordDeliveryPassResult();

        var connectedSystemIds = connectedSystemId.HasValue
            ? [connectedSystemId.Value]
            : await _syncRepo.GetConnectedSystemIdsWithDuePasswordChangesAsync(asOf);

        foreach (var id in connectedSystemIds)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var connectedSystem = await _connectedSystemRepo().GetConnectedSystemForPasswordDeliveryAsync(id);
            if (connectedSystem == null)
            {
                // Deleted between a change being queued and this pass reaching it. The queue rows go with the
                // system on delete, so there is nothing left to act on and nothing to tell anybody.
                Log.Debug("DeliverDueAsync: Connected System {ConnectedSystemId} no longer exists; skipping it.", id);
                continue;
            }

            // Read again here rather than trusted from whatever queued the work: Password Synchronisation may
            // have been disabled since, and a disabled system accumulates rather than delivers (requirement 2).
            if (connectedSystem.PasswordSynchronisation is not { Enabled: true })
                continue;

            IConnector connector;
            try
            {
                connector = _createConnector(connectedSystem);
            }
            catch (NotSupportedException ex)
            {
                // The Connected System names a Connector this build does not have. Reported rather than thrown,
                // and the queued changes are left exactly as they are: nothing was attempted against them.
                result.AddProblem($"{connectedSystem.Name}: its Connector could not be resolved, so queued password changes are waiting.");
                Log.Error(ex,
                    "DeliverDueAsync: Could not resolve the Connector for Connected System {ConnectedSystemId}. Its queued password change(s) are left outstanding.",
                    connectedSystem.Id);
                continue;
            }

            // IConnector carries no disposal contract, but concrete Connectors hold connections; disposing
            // what can be disposed keeps a pass over many systems from accumulating them. Null when the Connector
            // is not disposable, which using handles.
            using var disposableConnector = connector as IDisposable;

            var systemResult = await DeliverDuePasswordChangesAsync(connectedSystem, connector, asOf, cancellationToken);
            result.Add(connectedSystem.Name, systemResult);
        }

        Log.Information(
            "DeliverDueAsync: {Visited} Connected System(s) visited: {Delivered} delivered, {Retrying} retrying, {Parked} parked, {Expired} expired, {Problems} problem(s).",
            result.ConnectedSystemsVisited, result.DeliveredCount, result.RetryingCount, result.ParkedCount,
            result.ExpiredCount, result.Problems.Count);

        return result;
    }

    /// <summary>
    /// Makes every parked password change on a Connected System due again, returning how many were released:
    /// requirement 3's drain when a system is enabled, and the same mechanic when its delivery settings change.
    /// </summary>
    public async Task<int> ReleaseForDeliveryAsync(int connectedSystemId)
    {
        var released = await _syncRepo.ReleasePasswordChangesForDeliveryAsync(connectedSystemId);

        if (released > 0)
        {
            Log.Information(
                "ReleaseForDeliveryAsync: {Released} parked password change(s) on Connected System {ConnectedSystemId} are due again.",
                released, connectedSystemId);

            // Scoped to this system: the trigger knows exactly which one it released work on, so a pass over
            // every system would visit others with nothing to do.
            await _requestDelivery(connectedSystemId);
        }

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

    /// <summary>
    /// One window of the queue for a list view (requirement 21). The rows carry the identity and Connected
    /// System names and, deliberately, no password.
    /// </summary>
    public async Task<RangeResultSet<PendingPasswordChangeHeader>> GetPendingPasswordChangesAsync(
        PendingPasswordChangeFilter filter,
        int startIndex,
        int count,
        string sortBy,
        bool sortDescending,
        bool includeTotalCount)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return await _syncRepo.GetPendingPasswordChangeHeadersAsync(
            filter, startIndex, count, sortBy, sortDescending, includeTotalCount);
    }

    /// <summary>
    /// One identity's most recent password changes and what each Connected System did with them (#1119,
    /// requirement 25), newest change first.
    /// <para>
    /// Read from Activities rather than from the queue, deliberately. The queue row is deleted the moment the
    /// password arrives, so a panel built on the queue would show an identity's failures and none of its
    /// successes: the most misleading possible view of whether their password propagated.
    /// </para>
    /// </summary>
    public async Task<List<PasswordSynchronisationEvent>> GetEventsForMetaverseObjectAsync(Guid metaverseObjectId, int maximumEvents)
    {
        return await _activityRepo().GetPasswordSynchronisationEventsAsync(metaverseObjectId, maximumEvents);
    }

    /// <summary>
    /// What the whole queue holds, for the summary above a queue list.
    /// </summary>
    public async Task<PasswordQueueSummary> GetQueueSummaryAsync()
    {
        return await _syncRepo.GetPasswordQueueSummaryAsync(DateTime.UtcNow);
    }

    /// <summary>
    /// Makes every change matching <paramref name="filter"/> due immediately and raises a delivery pass for it:
    /// the queue page's retry action, and its REST and PowerShell counterparts (requirement 22).
    /// </summary>
    /// <returns>How many changes were made due again.</returns>
    public async Task<int> RetryAsync(
        PendingPasswordChangeFilter filter,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var retried = await _syncRepo.RetryPasswordChangesAsync(filter);

        // One Activity for the administrator's action, not one per row: a retry over a directory that has just
        // come back is a single decision, and a hundred Activities saying so would bury the decision in its own
        // consequences.
        await RecordQueueActionAsync(
            ActivityTargetOperationType.RetryPasswordDelivery,
            retried == 1
                ? "1 queued password change will be attempted again."
                : $"{retried} queued password changes will be attempted again.",
            filter,
            initiatedBy,
            initiatedByApiKey);

        if (retried > 0)
        {
            Log.Information("RetryAsync: {Retried} queued password change(s) are due again.", retried);

            // Scoped where the filter was, unscoped where it was not: a retry aimed at one system has no reason
            // to visit the others.
            await _requestDelivery(filter.ConnectedSystemId);
        }

        return retried;
    }

    /// <summary>
    /// Records that an administrator stopped every change matching <paramref name="filter"/> being delivered
    /// (requirement 22).
    /// </summary>
    /// <returns>How many changes were cancelled.</returns>
    public async Task<int> CancelAsync(
        PendingPasswordChangeFilter filter,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var cancelled = await _syncRepo.CancelPasswordChangesAsync(
            filter, initiatedBy?.Id, initiatedBy?.Name, DateTime.UtcNow);

        await RecordQueueActionAsync(
            ActivityTargetOperationType.CancelPasswordDelivery,
            cancelled == 1
                ? "1 queued password change will not be delivered."
                : $"{cancelled} queued password changes will not be delivered.",
            filter,
            initiatedBy,
            initiatedByApiKey);

        if (cancelled > 0)
            Log.Information("CancelAsync: {Cancelled} queued password change(s) were cancelled.", cancelled);

        return cancelled;
    }

    /// <summary>
    /// Records an administrator's action over the queue as one completed Activity.
    /// <para>
    /// Recorded even when nothing matched. An administrator who retried a system and changed nothing needs to be
    /// able to find that out afterwards, and an Activity that only appears when work happened cannot tell them
    /// the difference between "nothing was owed" and "the retry never ran".
    /// </para>
    /// </summary>
    private async Task RecordQueueActionAsync(
        ActivityTargetOperationType operation,
        string message,
        PendingPasswordChangeFilter filter,
        MetaverseObject? initiatedBy,
        ApiKey? initiatedByApiKey)
    {
        var activity = new Activity
        {
            TargetType = ActivityTargetType.PasswordSynchronisation,
            TargetOperationType = operation,
            TargetName = DescribeFilter(filter),
            ConnectedSystemId = filter.ConnectedSystemId,
            MetaverseObjectId = filter.MetaverseObjectId,
            Message = message
        };

        await _createActivity(activity, initiatedBy, initiatedByApiKey);
        await _completeActivity(activity);
    }

    /// <summary>
    /// Names what an action ran over, for the Activity's target. Says what the administrator chose rather than
    /// what it resolved to, because that is what they will look for later.
    /// </summary>
    private static string DescribeFilter(PendingPasswordChangeFilter filter)
    {
        if (filter.TargetsSpecificChanges)
            return filter.Ids!.Count == 1 ? "One password change" : $"{filter.Ids!.Count} password changes";

        var parts = new List<string>();

        if (filter.Status.HasValue)
            parts.Add(filter.Status.Value.ToString());

        if (filter.FailureReason.HasValue)
            parts.Add(filter.FailureReason.Value.ToString());

        return parts.Count == 0
            ? "The Password Synchronisation queue"
            : $"{string.Join(", ", parts)} password changes";
    }
}
