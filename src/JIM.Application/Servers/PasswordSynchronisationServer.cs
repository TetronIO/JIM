// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
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
/// The one password pipeline (#1119, #1635): turns a request to set a person's password into one queued change
/// per Connected System, whether the caller named the accounts (an administrator's reset) or asked for every
/// system configured for Password Synchronisation (a propagated change), and delivers the queue one lane per
/// system.
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
    private readonly Func<Activity, Task> _createSystemActivity;
    private readonly Func<Activity, Task> _completeActivity;
    private readonly Func<Activity, string, Task> _completeActivityWithError;

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
    /// <param name="createSystemActivity">
    /// Creates an Activity attributed to JIM itself, for work no person or API key asked for. Delivery is the
    /// case: a queued password change is delivered by a worker pass minutes or days after somebody queued it,
    /// and there is no principal at that moment to attribute the outcome to. The parent Activity for the change
    /// carries who made it; this records what one system did with it.
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
        Func<Activity, Task> createSystemActivity,
        Func<Activity, Task> completeActivity,
        Func<Activity, string, Task> completeActivityWithError)
    {
        _syncRepo = syncRepository;
        _connectedSystemRepo = connectedSystemRepository;
        _activityRepo = activityRepository;
        _passwordProtection = passwordProtection;
        _createConnector = createConnector;
        _createActivity = createActivity;
        _createSystemActivity = createSystemActivity;
        _completeActivity = completeActivity;
        _completeActivityWithError = completeActivityWithError;
    }

    /// <summary>
    /// Sets a person's password (#1635): the one operation behind Set Password on every surface, aimed either at
    /// the accounts the caller named or at every Connected System configured for Password Synchronisation.
    /// <para>
    /// Both target modes produce the same thing: one queued change per Connected System, coalesced onto anything
    /// already owed there, under one Activity for the request with a child per system recorded as each is
    /// delivered. Nothing is written to a directory here. The rows fire the queue's notification trigger, the
    /// Password Delivery Service claims them within a second, and this returns as soon as the intent is durable,
    /// whatever the directories are doing (decision D2).
    /// </para>
    /// <para>
    /// Named accounts are validated before anything is recorded: every id must be an account of this person in a
    /// system whose Connector can set passwords, and at most one per system. A request that fails validation
    /// leaves no Activity and no row, because nothing was asked for that JIM could do. A propagated change is
    /// never refused for having nowhere to go; a change that reached nothing is recorded saying so (requirement
    /// 14), since silence would let an administrator believe a password propagated when nothing was even queued.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The password is empty, the target list is empty, or a target is not one of this person's accounts, is in a
    /// system whose Connector cannot set passwords, or shares a Connected System with another target.
    /// </exception>
    public async Task<PasswordQueueResult> SetPasswordAsync(SetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Password))
            throw new ArgumentException("A password is required.", nameof(request));

        var plan = request.Targets == null
            ? await PlanPropagatedChangeAsync(request.MetaverseObjectId)
            : await PlanExplicitChangeAsync(request.MetaverseObjectId, request.Targets);

        var activity = new Activity
        {
            TargetName = request.DisplayName,
            TargetType = ActivityTargetType.PasswordSynchronisation,
            TargetOperationType = ActivityTargetOperationType.SetPassword,
            MetaverseObjectId = request.MetaverseObjectId,
            // The origin travels on the Activity because the Activity is what outlives the queue row: the person's
            // password history is read from Activities alone, and "set" against "propagated" is the one fact about
            // a change it could not otherwise recover. The enum's name rather than its number, so the read side
            // parses it back without a mapping table and a human reading the Activity list sees a word.
            TargetContext = request.Origin.ToString()
        };
        await _createActivity(activity, request.InitiatedBy, request.InitiatedByApiKey);

        var now = DateTime.UtcNow;
        var encrypted = _passwordProtection().ProtectPassword(request.Password)!;

        var changes = new List<PendingPasswordChange>(plan.Count);
        var outcomes = new List<PasswordQueueTargetOutcome>(plan.Count);
        foreach (var target in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            changes.Add(new PendingPasswordChange
            {
                MetaverseObjectId = request.MetaverseObjectId,
                ConnectedSystemId = target.ConnectedSystemId,
                // For a propagated change, null where the account does not exist yet, which is an ordinary state
                // rather than a failure: the change waits, bounded by its time to live, and delivery re-resolves
                // the account each attempt, so a password arriving before provisioning resolves itself (Resolved
                // Decision 2). For an explicit change, the account the administrator named.
                ConnectedSystemObjectId = target.ConnectedSystemObjectId,
                EncryptedPassword = encrypted,
                ExpiryBehaviour = request.ExpiryBehaviour,
                Origin = request.Origin,
                // Only an administrator who named the account gets to enable it. A propagated password reaches
                // accounts an administrator may have disabled on purpose, and re-enabling one would undo that
                // silently, as a side effect of somebody changing their password elsewhere.
                EnableAccount = request.Origin == PendingPasswordChangeOrigin.Explicit ? request.EnableAccount : null,
                CreatedAt = now,
                ExpiresAt = now + target.TimeToLive,
                ActivityId = activity.Id
            });

            outcomes.Add(new PasswordQueueTargetOutcome
            {
                ConnectedSystemId = target.ConnectedSystemId,
                ConnectedSystemName = target.ConnectedSystemName,
                Enabled = target.Enabled,
                ConnectedSystemObjectId = target.ConnectedSystemObjectId
            });
        }

        // One batched write for the whole fan-out, per the non-functional requirement that a change across ten
        // systems is a single write rather than ten round trips.
        if (changes.Count > 0)
            await _syncRepo.QueuePasswordChangesAsync(changes);

        activity.Message = DescribeQueueOutcome(outcomes, request.Origin);
        await _completeActivity(activity);

        // Synchronisation Integrity: summary statistics at the end of every batch operation. The systems are
        // named because that is what an administrator needs; the password is not, and no part of it ever is.
        Log.Information(
            "SetPasswordAsync: {Origin} password change for Metaverse Object {MetaverseObjectId} queued for {TargetCount} Connected System(s), " +
            "{PausedCount} of which are not currently taking propagated passwords: {Targets}",
            request.Origin, request.MetaverseObjectId, outcomes.Count, outcomes.Count(o => !o.Enabled),
            outcomes.Count == 0 ? "none" : LogSanitiser.Sanitise(string.Join(", ", outcomes.Select(o => o.ConnectedSystemName))));

        return new PasswordQueueResult { ActivityId = activity.Id, Origin = request.Origin, Targets = outcomes };
    }

    /// <summary>
    /// Where a password change is going: one entry per Connected System, resolved before anything is recorded.
    /// </summary>
    private sealed record PlannedTarget(
        int ConnectedSystemId,
        string ConnectedSystemName,
        Guid? ConnectedSystemObjectId,
        bool Enabled,
        TimeSpan TimeToLive);

    /// <summary>
    /// Every Connected System configured for Password Synchronisation, with the person's account of the type each
    /// one nominated (#1119, requirement 6).
    /// </summary>
    private async Task<List<PlannedTarget>> PlanPropagatedChangeAsync(Guid metaverseObjectId)
    {
        var connectedSystems = _connectedSystemRepo();

        // Every system configured for Password Synchronisation, including those switched off. One that is off
        // accumulates the change rather than discarding it (requirement 2), and enabling it delivers what
        // accumulated (requirement 3); skipping it here would throw the password away at the only moment it
        // could have been kept, leaving nothing behind for anybody to notice.
        var targets = await connectedSystems.GetPasswordSynchronisationTargetsAsync();

        // The identity's accounts, read once and matched against every target, rather than a query per system.
        var accounts = await connectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId);

        return targets.Select(target =>
        {
            // The identity's account of the type this system nominated. An identity can hold a Connected System
            // Object of another type in the same system, and a password belongs to the account.
            var account = accounts.SingleOrDefault(a =>
                a.ConnectedSystemId == target.ConnectedSystemId && a.TypeId == target.TargetObjectTypeId);

            return new PlannedTarget(target.ConnectedSystemId, target.ConnectedSystemName, account?.Id, target.Enabled, target.TimeToLive);
        }).ToList();
    }

    /// <summary>
    /// The accounts an administrator named, each checked to be this person's, in a system whose Connector can set
    /// passwords, and alone in its system (#1635).
    /// <para>
    /// No Password Synchronisation configuration is required, and a paused one does not hold the change (decision
    /// D1): the administrator has made the decision a configuration exists to make. The time to live comes from
    /// the Connected System, as it does for every queued password.
    /// </para>
    /// <para>
    /// The error messages deliberately carry no parameter name: they are shown to an administrator and returned by
    /// the REST API, where "(Parameter 'request')" is noise about JIM's own method signature.
    /// </para>
    /// </summary>
    private async Task<List<PlannedTarget>> PlanExplicitChangeAsync(Guid metaverseObjectId, IReadOnlyList<Guid> targets)
    {
        if (targets.Count == 0)
            throw new ArgumentException("At least one account is required. To set the password on every Connected System configured for Password Synchronisation, name no accounts at all.");

        var connectedSystems = _connectedSystemRepo();
        var accounts = (await connectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId))
            .ToDictionary(a => a.Id);

        var systems = new Dictionary<int, ConnectedSystem>();
        var plan = new List<PlannedTarget>(targets.Count);
        var accountBySystem = new Dictionary<int, Guid>();

        foreach (var connectedSystemObjectId in targets.Distinct())
        {
            if (!accounts.TryGetValue(connectedSystemObjectId, out var account))
                throw new ArgumentException($"Connected System Object {connectedSystemObjectId} is not one of this Metaverse Object's accounts.");

            if (!systems.TryGetValue(account.ConnectedSystemId, out var system))
            {
                // Loaded with its Connector Definition, so the capability check below is the same check the
                // account list makes: the Connector's code, not a flag that could have gone stale.
                system = await connectedSystems.GetConnectedSystemForPasswordDeliveryAsync(account.ConnectedSystemId)
                    ?? throw new ArgumentException($"Connected System {account.ConnectedSystemId}, which holds Connected System Object {connectedSystemObjectId}, does not exist.");

                var connector = _createConnector(system);
                using var disposableConnector = connector as IDisposable;
                if (connector is not IConnectorPasswordManagement)
                    throw new ArgumentException($"Connected System Object {connectedSystemObjectId} is in {system.Name}, whose Connector cannot set passwords.");

                systems[system.Id] = system;
            }

            // The queue holds one change per person per system, so two accounts in one system would coalesce
            // into one row and the first account would silently never get the password. Refused rather than
            // guessed at: the administrator chose both, and only they can say which.
            if (accountBySystem.TryGetValue(system.Id, out var other))
                throw new ArgumentException($"Connected System Objects {other} and {connectedSystemObjectId} are both in {system.Name}; a password can be set on one account per Connected System at a time.");

            accountBySystem[system.Id] = connectedSystemObjectId;
            plan.Add(new PlannedTarget(
                system.Id,
                system.Name,
                connectedSystemObjectId,
                system.PasswordSynchronisation is { Enabled: true },
                system.EffectiveInitialPasswordTimeToLive));
        }

        return plan;
    }

    /// <summary>
    /// What the Activity says about where a password change went (#1119, requirement 14; #1635).
    /// <para>
    /// For a propagated change the distinction the message has to carry is between queued-and-on-its-way and
    /// queued-but-held: a system that is configured and switched off accumulates the change, and an administrator
    /// reading the Activity weeks later needs to know that a password is sitting waiting on somebody turning that
    /// system back on, rather than assuming everything named here has the password already. An explicit set is
    /// never held, so its message names the accounts and stops.
    /// </para>
    /// </summary>
    internal static string DescribeQueueOutcome(IReadOnlyList<PasswordQueueTargetOutcome> outcomes, PendingPasswordChangeOrigin origin)
    {
        if (origin == PendingPasswordChangeOrigin.Explicit)
        {
            return $"Password set requested for {outcomes.Count} account{(outcomes.Count == 1 ? string.Empty : "s")}: " +
                   string.Join(", ", outcomes.Select(o => o.ConnectedSystemName)) + ".";
        }

        if (outcomes.Count == 0)
            return "No Connected System is configured for Password Synchronisation, so this password was not queued for delivery anywhere.";

        var message = $"Password change queued for {outcomes.Count} Connected System{(outcomes.Count == 1 ? string.Empty : "s")}: " +
                      string.Join(", ", outcomes.Select(o => o.ConnectedSystemName)) + ".";

        var held = outcomes.Where(o => !o.Enabled).Select(o => o.ConnectedSystemName).ToList();
        if (held.Count == 0)
            return message;

        return message + $" Held until Password Synchronisation is enabled on {string.Join(", ", held)}" +
               $"; the change{(outcomes.Count == 1 ? string.Empty : "s")} will be delivered then, or expire first.";
    }

    /// <summary>
    /// How many queued password changes one lane will attempt against a Connected System before handing back.
    /// <para>
    /// A bound rather than a page size, matching the initial-password pass: a misconfigured target must not turn
    /// one lane into an unbounded run of failing round trips. What is left over is taken by the next lane, oldest
    /// first, so nothing starves; the rows the lane wrote wake the service for it.
    /// </para>
    /// </summary>
    public const int MaximumChangesPerPass = 1000;

    /// <summary>
    /// How many changes a lane claims at a time. Small enough that a claim is never held for much longer than the
    /// attempts it covers, so a lane working through a long queue against a slow directory does not outlive its
    /// own lease (<see cref="ClaimLease"/>) on the rows at the back of the batch; large enough that the claim
    /// round trip is a small fraction of the directory work between claims.
    /// </summary>
    public const int ClaimBatchSize = 100;

    /// <summary>
    /// How long a claim is honoured before another deliverer may take the change. <see cref="PendingPasswordChange.ClaimLease"/>,
    /// surfaced here because this is the layer that passes it to every read that must agree on it.
    /// </summary>
    public static readonly TimeSpan ClaimLease = PendingPasswordChange.ClaimLease;

    /// <summary>
    /// Delivers the password changes due on a Connected System, expiring anything that has outlived its window
    /// first: one lane of the Password Delivery Service (#1635).
    /// <para>
    /// Works over rows it claims rather than rows it reads. Each claim marks its rows Delivering under
    /// <paramref name="claimedBy"/> in the same statement that selects them, so a second deliverer (another
    /// Worker replica, or a lane overlapping a safety poll) cannot take the same rows; the claim is released
    /// unattempted wherever the lane finds it cannot deliver at all, so nothing is counted against a change
    /// nobody tried.
    /// </para>
    /// <para>
    /// Never throws for a delivery that did not work. Every outcome is classified and recorded against the change
    /// it belongs to, because a lane that threw would abandon the changes it had not reached and lose the outcomes
    /// of the ones it had.
    /// </para>
    /// </summary>
    /// <param name="connectedSystem">The Connected System to deliver to, loaded with its configuration.</param>
    /// <param name="connector">
    /// Its Connector. One that cannot set passwords leaves the queued changes exactly as they are rather than
    /// failing them: the capability may arrive with a Connector upgrade.
    /// </param>
    /// <param name="claimedBy">
    /// Who is delivering: the service instance id, stamped on every row this lane claims so a row found stuck in
    /// Delivering names the process to look at.
    /// </param>
    /// <param name="asOf">The instant the lane runs, for expiry and retry scheduling.</param>
    /// <param name="cancellationToken">
    /// Stops before the changes not yet reached, releasing their claims. It cannot undo the deliveries already
    /// made, and their outcomes are still recorded: a password that landed has landed whatever the lane did next.
    /// </param>
    public async Task<PasswordDeliveryRunResult> DeliverDuePasswordChangesAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        string claimedBy,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);

        return await DeliverLaneAsync(connectedSystem, () => connector, claimedBy, asOf, cancellationToken);
    }

    /// <summary>
    /// The lane itself, with the Connector resolved only once there is something claimed to deliver. A system
    /// visited with nothing due (a paused system whose explicit sets have all landed, a safety poll finding the
    /// queue drained) never has a Connector built for it, and a Connector this build no longer has is discovered
    /// with the claimed rows in hand, so they can be given back unattempted and the problem reported by name.
    /// </summary>
    private async Task<PasswordDeliveryRunResult> DeliverLaneAsync(
        ConnectedSystem connectedSystem,
        Func<IConnector> resolveConnector,
        string claimedBy,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

        var result = new PasswordDeliveryRunResult();

        // Which rows this lane may take (#1635, decision D1). A system whose Password Synchronisation is
        // unconfigured or switched off delivers no propagated change: requirement 2 has it accumulate rather than
        // discard, so enabling it later has something to drain. An administrator's explicit set is delivered
        // there anyway, because the administrator named the account and has already made the decision a
        // configuration exists to make. So the lane claims only explicit rows over such a system, and everything
        // due over a live one.
        var explicitOnly = connectedSystem.PasswordSynchronisation is not { Enabled: true };

        // The retry policy. A system with no configuration at all still delivers explicit sets, under JIM's
        // defaults; the transient instance carries those and is never persisted, so nothing here creates a
        // configuration the administrator did not.
        var configuration = connectedSystem.PasswordSynchronisation
                            ?? new ConnectedSystemPasswordSynchronisation { ConnectedSystemId = connectedSystem.Id };

        // Expiry first, so a change on its way out is not attempted, and its attempt count not inflated, on the
        // very lane that retires it. Expiry touches Pending rows only; a claimed row belongs to its claimant. Over
        // a paused system only the explicit rows are retired: the held propagated ones are left exactly as they
        // were, to be expired or delivered by the first lane after the system is switched back on.
        result.ExpiredCount = await _syncRepo.ExpirePasswordChangesAsync(connectedSystem.Id, asOf, explicitOnly);

        var claimed = await _syncRepo.ClaimDuePasswordChangesAsync(connectedSystem.Id, claimedBy, asOf, ClaimLease, ClaimBatchSize, explicitOnly);
        if (claimed.Count == 0)
            return result;

        IConnector connector;
        try
        {
            connector = resolveConnector();
        }
        catch (NotSupportedException ex)
        {
            // The Connected System names a Connector this build does not have. Reported rather than thrown, and
            // the claimed changes are given back exactly as they were: nothing was attempted against them.
            result.ConnectorCouldNotBeResolved = true;
            Log.Error(ex,
                "DeliverDuePasswordChangesAsync: Could not resolve the Connector for Connected System {ConnectedSystemId}. {Count} queued password change(s) are left outstanding.",
                connectedSystem.Id, claimed.Count);
            await ReleaseUnattemptedAsync(claimed);
            return result;
        }

        if (connector is not IConnectorPasswordManagement passwordConnector)
        {
            result.ConnectorCannotSetPasswords = true;
            Log.Warning(
                "DeliverDuePasswordChangesAsync: The Connector for Connected System {ConnectedSystemId} cannot set passwords. {Count} queued password change(s) are left outstanding.",
                connectedSystem.Id, claimed.Count);
            await ReleaseUnattemptedAsync(claimed);
            return result;
        }

        // Open once for the lane, and refuse once for the lane: a channel that could not be opened, or that the
        // system's Require Secure Transport setting forbids, is one problem for an administrator to fix rather
        // than one per change, and counting it against every change would inflate attempt counts that are
        // supposed to mean "distinct attempts at this password". Nothing is attempted and no attempt is counted:
        // the changes go back to Pending and due, so fixing the cause is enough for the next lane to deliver them.
        var opening = PasswordDeliveryCore.OpenChannel(passwordConnector, connectedSystem);
        if (!opening.IsOpen)
        {
            result.CouldNotOpenPasswordConnection = opening.CouldNotOpenChannel;
            result.PasswordChannelNotSecure = opening.ChannelNotSecure;
            Log.Warning(
                "DeliverDuePasswordChangesAsync: The password channel to Connected System {ConnectedSystemId} is not usable ({Reason}). {Count} queued password change(s) are left outstanding.",
                connectedSystem.Id, opening.ChannelNotSecure ? "not encrypted, and the system requires a secure transport" : "could not be opened", claimed.Count);
            await ReleaseUnattemptedAsync(claimed);
            return result;
        }

        try
        {
            var attemptedInTotal = 0;
            while (true)
            {
                attemptedInTotal += await DeliverBatchAsync(passwordConnector, connectedSystem, configuration, claimed, asOf, result, cancellationToken);

                // Another batch only when this one was full and the bound has room: a short batch means the queue
                // for this system is drained as of the claim, and anything queued since has woken the service.
                if (cancellationToken.IsCancellationRequested || claimed.Count < ClaimBatchSize || attemptedInTotal >= MaximumChangesPerPass)
                    break;

                claimed = await _syncRepo.ClaimDuePasswordChangesAsync(connectedSystem.Id, claimedBy, asOf, ClaimLease,
                    Math.Min(ClaimBatchSize, MaximumChangesPerPass - attemptedInTotal), explicitOnly);
                if (claimed.Count == 0)
                    break;
            }
        }
        finally
        {
            passwordConnector.ClosePasswordConnection();
        }

        // Synchronisation Integrity: summary statistics at the end of every batch operation.
        Log.Information(
            "DeliverDuePasswordChangesAsync: Connected System {ConnectedSystemId}: {Delivered} delivered, {Retrying} retrying, {Parked} parked, {Expired} expired, {Released} released unattempted.",
            connectedSystem.Id, result.DeliveredCount, result.RetryingCount, result.ParkedCount, result.ExpiredCount, result.ReleasedCount);

        return result;
    }

    /// <summary>
    /// Attempts one claimed batch, persisting what happened to every row in it whatever happens: outcomes for the
    /// rows attempted, deletion for the rows delivered, and release for the rows a cancellation stopped the lane
    /// reaching. Returns how many were attempted.
    /// </summary>
    private async Task<int> DeliverBatchAsync(
        IConnectorPasswordManagement passwordConnector,
        ConnectedSystem connectedSystem,
        ConnectedSystemPasswordSynchronisation configuration,
        List<PendingPasswordChange> claimed,
        DateTime asOf,
        PasswordDeliveryRunResult result,
        CancellationToken cancellationToken)
    {
        var attempted = new List<PendingPasswordChange>();
        var delivered = new List<Guid>();
        var unattempted = new List<PendingPasswordChange>();

        try
        {
            foreach (var change in claimed)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    unattempted.Add(change);
                    continue;
                }

                // A claim can outlive a change's window when it was reclaimed from a deliverer that died holding
                // it: expiry never touches a Delivering row, so this is where such a row is retired. Recorded
                // through the attempt write, which is guarded on the claim, rather than attempted.
                if (change.ExpiresAt <= asOf)
                {
                    change.Expire();
                    attempted.Add(change);
                    result.ExpiredCount++;
                    continue;
                }

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
            // Persisted in the finally so a cancelled lane keeps what it achieved. Re-delivering a password
            // already set is harmless, but leaving a delivered change queued would send it again on every lane,
            // and leaving a claimed one claimed would hold it for the whole lease.
            if (attempted.Count > 0)
                await _syncRepo.RecordPasswordChangeAttemptsAsync(attempted);

            if (delivered.Count > 0)
                await _syncRepo.DeletePasswordChangesAsync(delivered);

            if (unattempted.Count > 0)
                result.ReleasedCount += await ReleaseClaimsAsync(unattempted);
        }

        return attempted.Count + delivered.Count;
    }

    /// <summary>
    /// Gives a whole claimed batch back unattempted, for a lane that found before its first attempt that it could
    /// not deliver at all. Logged rather than counted on the result, because the result already names why.
    /// </summary>
    private async Task ReleaseUnattemptedAsync(List<PendingPasswordChange> claimed)
    {
        var released = await ReleaseClaimsAsync(claimed);
        Log.Debug("DeliverDuePasswordChangesAsync: Released {Released} of {Claimed} claimed password change(s) unattempted.", released, claimed.Count);
    }

    private async Task<int> ReleaseClaimsAsync(List<PendingPasswordChange> claimed)
    {
        foreach (var change in claimed)
            change.ReleaseClaim();

        return await _syncRepo.ReleasePasswordChangeClaimsAsync(claimed.Select(c => c.Id));
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
        // what lets a propagated change queued before provisioning deliver once the account appears, and what
        // stops any change being sent to an account that has since been deleted and replaced.
        var accounts = await _connectedSystemRepo().GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId);

        ConnectedSystemObject? account;
        if (change.IsExplicit)
        {
            // The administrator named this account, so it is the only one that will do. Re-read among the
            // person's accounts rather than by id alone, so an account that has been deleted (the foreign key
            // nulls the row's account on delete) or disjoined from the person since is not written to.
            account = change.ConnectedSystemObjectId is { } namedAccountId
                ? accounts.SingleOrDefault(a => a.Id == namedAccountId && a.ConnectedSystemId == connectedSystem.Id)
                : null;

            if (account == null)
            {
                // Parked rather than retried: unlike a propagated change, which waits for provisioning to catch
                // up, an explicit set has nothing to wait for. The account it was for is gone, and only a person
                // can decide what to do about that.
                change.RecordAttempt(PasswordSetFailureReason.TargetObjectNotFound,
                    $"The account this password was set for no longer exists in {connectedSystem.Name}, or is no longer joined to this person.",
                    configuration, asOf);
                change.Status = PendingPasswordChangeStatus.Parked;
                change.NextRetryAt = null;
                await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
                return false;
            }
        }
        else
        {
            account = accounts.SingleOrDefault(a =>
                a.ConnectedSystemId == connectedSystem.Id && a.TypeId == configuration.TargetObjectTypeId);

            if (account == null)
            {
                // Retry rather than park: the account may simply not have been provisioned yet, and the change's
                // own time to live is what bounds the wait (Resolved Decision 2).
                change.RecordAttempt(PasswordSetFailureReason.TargetObjectNotFound,
                    "The identity has no account in this Connected System yet.", configuration, asOf);
                await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
                return false;
            }

            change.ConnectedSystemObjectId = account.Id;
        }

        string password;
        try
        {
            // The one point at which a queued password exists in cleartext, and only for this attempt.
            password = _passwordProtection().UnprotectPassword(change.EncryptedPassword)!;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // The key ring has been rotated or lost, or the stored value is not one this deployment wrote, so this
            // change can never be decrypted. Parked rather than retried: no number of attempts recovers a value
            // nothing can read.
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

        // A Connector that throws rather than classifying comes back from the core as a transient failure, so the
        // change is kept and one target's fault never stops the lane reaching the others.
        var setResult = await PasswordDeliveryCore.SetPasswordAsync(passwordConnector, account, password, new PasswordSetOptions
        {
            ExpiryBehaviour = change.ExpiryBehaviour,
            // Only an administrator who named the account may enable it. A propagated password reaches accounts
            // an administrator may have disabled on purpose, and re-enabling one would undo that silently, as a
            // side effect of somebody changing their password elsewhere; enabling on provisioning belongs to the
            // initial password.
            EnableAccount = change.IsExplicit ? change.EnableAccount : null
        }, cancellationToken);

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

        // System-attributed, not "attributed to nobody". Every Activity must name a principal, and passing no
        // initiator here made the Activity server refuse it: the refusal threw out of the delivery pass, so an
        // outcome could never be recorded and the change was retried for ever. Delivery runs unattended, long
        // after whoever queued the change has gone, so JIM itself is the honest principal; the parent Activity
        // still carries the person or API key that made the password change.
        await _createSystemActivity(activity);

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
    /// How many Connected Systems are configured to receive synchronised passwords (#1119).
    /// <para>
    /// Read by the portal so it can say how many systems a propagated password will reach, rather than offering
    /// the propagate mode and having it turn out to reach nothing once somebody has typed a password into it.
    /// </para>
    /// <para>
    /// Configured rather than enabled, because a system that is switched off does not make the action pointless:
    /// the change is queued and waits for it to come back (requirement 2). Counting only the enabled ones would
    /// withdraw the action during exactly the outage it exists to survive, and the password change made during
    /// that window would be the one nobody could record.
    /// </para>
    /// </summary>
    public async Task<int> GetTargetCountAsync()
    {
        var targets = await _connectedSystemRepo().GetPasswordSynchronisationTargetsAsync();
        return targets.Count;
    }

    /// <summary>
    /// Which Connected Systems have password work a lane would attempt now (#1635): pending and due on an enabled
    /// system, or claimed under a lease that has run out. What the Password Delivery Service asks on every wake
    /// to decide which lanes to run.
    /// </summary>
    public async Task<List<int>> GetConnectedSystemIdsWithWorkDueAsync(DateTime asOf)
    {
        return await _syncRepo.GetConnectedSystemIdsWithDuePasswordChangesAsync(asOf, ClaimLease);
    }

    /// <summary>
    /// What the Password Delivery Service has ahead of it (#1635): due and retrying counts and the earliest
    /// scheduled attempt, in one query. The service sleeps until that attempt (or its safety poll, whichever is
    /// sooner) and writes the counts into its heartbeat.
    /// </summary>
    public async Task<PasswordQueueDeliveryOutlook> GetDeliveryOutlookAsync(DateTime asOf)
    {
        return await _syncRepo.GetPasswordQueueDeliveryOutlookAsync(asOf, ClaimLease);
    }

    /// <summary>
    /// Runs a delivery pass over the Connected Systems with password work due, resolving each system's Connector
    /// as it goes. This is what the Password Delivery Service calls, one system per lane.
    /// <para>
    /// A system that cannot be delivered to is recorded and stepped over rather than thrown from. A pass that
    /// threw on the first unreachable directory would leave every system behind it in the list undelivered, which
    /// is exactly the failure mode Password Synchronisation exists to avoid: somebody's password differing
    /// between systems because one of them happened to be down.
    /// </para>
    /// </summary>
    /// <param name="connectedSystemId">
    /// The Connected System to deliver to, or null to visit every system with work due. Named where the caller
    /// knows which system it is, so a targeted delivery does not sweep systems with nothing to do.
    /// </param>
    /// <param name="claimedBy">Who is delivering: the service instance id, stamped on every row claimed.</param>
    /// <param name="asOf">The moment the pass is running as of; what is due and what has expired are read from it.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the pass.</param>
    public async Task<PasswordDeliveryPassResult> DeliverDueAsync(int? connectedSystemId, string claimedBy, DateTime asOf, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

        var result = new PasswordDeliveryPassResult();

        var connectedSystemIds = connectedSystemId.HasValue
            ? [connectedSystemId.Value]
            : await GetConnectedSystemIdsWithWorkDueAsync(asOf);

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

            // The system's configuration is read again here rather than trusted from whatever queued the work:
            // Password Synchronisation may have been switched off since, and the lane holds propagated changes
            // back on a switched-off system while still delivering an administrator's explicit sets (#1635,
            // decision D1). The Connector is built only if the lane claims something, so a system on the due list
            // with nothing this lane may take costs a claim and no more.
            IConnector? connector = null;
            try
            {
                var systemResult = await DeliverLaneAsync(connectedSystem, () => connector = _createConnector(connectedSystem), claimedBy, asOf, cancellationToken);

                // Counted as visited only where the lane found something to do or report. A system with nothing
                // claimable is stepped over silently, as it always was.
                if (systemResult.HasSomethingToReport)
                    result.Add(connectedSystem.Name, systemResult);
            }
            finally
            {
                // IConnector carries no disposal contract, but concrete Connectors hold connections; disposing
                // what can be disposed keeps a pass over many systems from accumulating them.
                (connector as IDisposable)?.Dispose();
            }
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
            Log.Information(
                "ReleaseForDeliveryAsync: {Released} parked password change(s) on Connected System {ConnectedSystemId} are due again.",
                released, connectedSystemId);

        // Nothing asks for delivery here. Where rows were un-parked, their update fires the queue's notification
        // trigger and the Password Delivery Service wakes for them. Where none were (a system switched on after a
        // spell off has everything already Pending and due), the service's next wake finds the system among
        // those with work due: enabling it is what made its accumulated changes count, and the safety poll is at
        // most thirty seconds away.
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
    /// Where one password change stands at every Connected System it was queued for (#1635), or null where no
    /// Activity with that id exists. What a caller waiting on a change polls, and what the dialog and the REST
    /// response are built from.
    /// <para>
    /// Merged from the queue rows still carrying this change and the child Activities recorded under it, because
    /// neither alone answers the question: the row is deleted when the password lands, so the queue never shows a
    /// success, and the Activities say nothing about a change still waiting its turn.
    /// </para>
    /// </summary>
    public async Task<PasswordChangeOutcomes?> GetChangeOutcomesAsync(Guid activityId)
    {
        var activity = await _activityRepo().GetActivityAsync(activityId);
        if (activity == null)
            return null;

        var rows = await _syncRepo.GetPasswordChangesByActivityAsync(activityId);
        var outcomes = await _activityRepo().GetPasswordSynchronisationOutcomesAsync(activityId);

        // Names and the enabled flag for every configured system, in one read. A propagated row on a paused
        // system is Held rather than Queued, and that is a fact about the system's configuration now, not about
        // the row.
        var targets = (await _connectedSystemRepo().GetPasswordSynchronisationTargetsAsync())
            .ToDictionary(t => t.ConnectedSystemId);

        var newestOutcomeBySystem = outcomes
            .Where(o => o.ConnectedSystemId.HasValue)
            .GroupBy(o => o.ConnectedSystemId!.Value)
            .ToDictionary(g => g.Key, g => (Newest: g.Last(), Count: g.Count()));

        var rowsBySystem = rows.ToDictionary(r => r.ConnectedSystemId);

        var systemIds = rowsBySystem.Keys.Union(newestOutcomeBySystem.Keys);
        var results = new List<PasswordChangeTargetOutcome>();
        foreach (var systemId in systemIds)
        {
            targets.TryGetValue(systemId, out var target);
            rowsBySystem.TryGetValue(systemId, out var row);
            newestOutcomeBySystem.TryGetValue(systemId, out var history);

            // An explicit set can be queued for a system with no Password Synchronisation configuration (#1635),
            // which the targets read above does not cover; its name is read on its own rather than shown as a
            // number. Rare, and one small read per such system.
            var name = target?.ConnectedSystemName
                       ?? history.Newest?.ConnectedSystemName
                       ?? (await _connectedSystemRepo().GetConnectedSystemHeaderAsync(systemId))?.Name
                       ?? $"Connected System {systemId}";

            results.Add(row != null
                ? DescribeRow(systemId, name, row, target is { Enabled: true })
                : DescribeHistory(systemId, name, history.Newest!, history.Count));
        }

        var ordered = results.OrderBy(r => r.ConnectedSystemName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ConnectedSystemId).ToList();

        return new PasswordChangeOutcomes
        {
            ActivityId = activity.Id,
            MetaverseObjectId = activity.MetaverseObjectId ?? Guid.Empty,
            Created = activity.Created,
            IsSettled = ordered.All(r => r.State is not (PasswordChangeTargetState.Queued or PasswordChangeTargetState.Delivering)),
            Targets = ordered
        };
    }

    /// <summary>
    /// A target still carrying a queue row, described from the row: it is the authority on what JIM intends to do
    /// next, and the most recent attempt's words are on it.
    /// </summary>
    private static PasswordChangeTargetOutcome DescribeRow(int systemId, string name, PendingPasswordChange row, bool systemEnabled)
    {
        var state = row.Status switch
        {
            PendingPasswordChangeStatus.Delivering => PasswordChangeTargetState.Delivering,
            PendingPasswordChangeStatus.Parked => PasswordChangeTargetState.Parked,
            PendingPasswordChangeStatus.Expired => PasswordChangeTargetState.Expired,
            PendingPasswordChangeStatus.Cancelled => PasswordChangeTargetState.Cancelled,
            // Pending: held by a paused system, waiting out a backoff after an attempt, or not yet attempted. Only
            // a propagated change is ever held; an explicit set is delivered on a paused system (decision D1).
            _ when !systemEnabled && !row.IsExplicit => PasswordChangeTargetState.Held,
            _ when row.LastAttemptedAt != null => PasswordChangeTargetState.Retrying,
            _ => PasswordChangeTargetState.Queued
        };

        return new PasswordChangeTargetOutcome
        {
            ConnectedSystemId = systemId,
            ConnectedSystemName = name,
            State = state,
            NextAttemptAt = state == PasswordChangeTargetState.Retrying ? row.NextRetryAt : null,
            Message = row.TargetMessage ?? (row.FailureReason?.ToString()),
            // None is the row's "no failure yet" and reads as null here, so a caller can key guidance on the
            // presence of a reason rather than on a sentinel.
            FailureReason = row.FailureReason is null or PasswordSetFailureReason.None ? null : row.FailureReason,
            OccurredAt = row.LastAttemptedAt,
            AttemptCount = row.AttemptCount
        };
    }

    /// <summary>
    /// A target with no queue row left, described from its newest delivery Activity. A success means the password
    /// was set and the row deleted. A failure with no row behind it means the row has since been removed by
    /// retention after parking; the last thing known about it was that refusal, so it reads as Parked.
    /// </summary>
    private static PasswordChangeTargetOutcome DescribeHistory(int systemId, string name, PasswordSynchronisationEventOutcome newest, int attemptCount)
    {
        var set = newest.Succeeded == true;
        return new PasswordChangeTargetOutcome
        {
            ConnectedSystemId = systemId,
            ConnectedSystemName = name,
            State = set ? PasswordChangeTargetState.Set : PasswordChangeTargetState.Parked,
            Message = set ? newest.Message : newest.ErrorMessage ?? newest.Message,
            OccurredAt = newest.OccurredAt,
            AttemptCount = attemptCount
        };
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

        // The rows just made due fire the queue's notification trigger, which wakes the Password Delivery
        // Service; nothing else is needed to have them attempted within a second.
        if (retried > 0)
            Log.Information("RetryAsync: {Retried} queued password change(s) are due again.", retried);

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
    /// Removes queued password changes that reached a terminal state (parked, expired, or cancelled) and have
    /// since had their retention period, and returns how many were removed (requirement 28). Changes still owed
    /// to a Connected System are never removed, however old.
    /// <para>
    /// Parked, expired and cancelled rows are kept on purpose: an identity whose password never reached a system
    /// must say so rather than disappear, which is the silent divergence this feature exists to prevent. Kept for
    /// ever, though, they are unbounded growth, and each one still carries an encrypted password. This is the
    /// other end of that decision, and the retention period is what bounds how long JIM holds a password it can
    /// no longer deliver.
    /// </para>
    /// <para>
    /// Called by the History Retention Cleanup Schedule under
    /// <see cref="Constants.SettingKeys.PasswordEventRetentionPeriod"/> and the shared cleanup batch size, in the
    /// same pass that trims the Activities recording what happened to each change.
    /// </para>
    /// </summary>
    /// <param name="olderThan">The retention cutoff; rows terminal before this are eligible.</param>
    /// <param name="maxRecords">The most to remove in one pass.</param>
    public async Task<int> DeleteExpiredQueueRecordsAsync(DateTime olderThan, int maxRecords)
    {
        var deleted = await _syncRepo.DeleteTerminalPasswordChangesAsync(olderThan, maxRecords);

        if (deleted > 0)
            Log.Information("DeleteExpiredQueueRecordsAsync: Removed {Count} queued password change(s) that had been " +
                "parked, expired or cancelled since before {OlderThan}", deleted, olderThan);

        return deleted;
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
