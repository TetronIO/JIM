// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data.Repositories;
using System.Security.Cryptography;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
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
    private readonly Func<Activity, Task> _completeActivityWithWarning;
    private readonly IPasswordGeneratorService _passwordGenerator;
    private readonly Func<ICredentialProtectionService> _credentialProtection;

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
    /// <param name="completeActivityWithWarning">
    /// Completes an Activity with a warning: for a delivery that set the password but where the Connected System
    /// could not apply everything asked of it, such as an expiry behaviour the directory has no equivalent for
    /// (#2159). The password is set, so it is not a failure; recorded as a plain completion, the difference would be
    /// invisible to everything that counts, filters or alerts on outcomes.
    /// </param>
    /// <param name="passwordGenerator">
    /// Generates a Provisioned row's first password from its Synchronisation Rule's settings at the moment of
    /// delivery (#1697): mirrors <see cref="InitialPasswordServer"/>, which resolves the same way
    /// for the pass that preceded this one.
    /// </param>
    /// <param name="credentialProtection">
    /// Decrypts a Provisioned row's static password, resolved when a delivery is attempted rather than now, for
    /// the same reason <paramref name="passwordProtection"/> is: the hosts assign
    /// <see cref="JimApplication.CredentialProtection"/> after constructing the facade.
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
        Func<Activity, string, Task> completeActivityWithError,
        Func<Activity, Task> completeActivityWithWarning,
        IPasswordGeneratorService passwordGenerator,
        Func<ICredentialProtectionService> credentialProtection)
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
        _completeActivityWithWarning = completeActivityWithWarning;
        _passwordGenerator = passwordGenerator;
        _credentialProtection = credentialProtection;
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
    /// The password is empty, the request asks to disable the account (#2162), the request names no accounts and asks for
    /// a password that never expires (#2159), the target list is empty, or a target is
    /// not one of this person's accounts, is in a system whose Connector cannot set passwords, or shares a Connected
    /// System with another target.
    /// </exception>
    public async Task<PasswordQueueResult> SetPasswordAsync(SetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Password))
            throw new ArgumentException("A password is required.", nameof(request));

        // Setting a password enables an account or leaves it as it is; it never disables one (#2162). A disable here
        // would override the account's Attribute Flows as a side effect of a password reset, and nothing that sets a
        // password asks for one: the portal and PowerShell offer enable or nothing. Refused rather than carried to the
        // Connector. No parameter name, because the message goes to API callers as it is.
        if (request.EnableAccount == false)
        {
            throw new ArgumentException(
                "enableAccount accepts true, to enable the account as its password is set, or can be left out to leave the account's enabled state as it is. Setting a password never disables an account.");
        }

        // A password set on every Connected System leaves "Password never expires" as each account has it (#2159): it
        // reaches accounts whose userAccountControl an Attribute Flow writes, or an administrator exempted from expiry on
        // purpose, and the flag belongs to them. Never expires there would be accepted and then do nothing, so it is
        // refused; naming the accounts is how to choose it. Refused here rather than in each surface, so every caller
        // gets the same answer, and like the disable refusal, before anything is queued or recorded.
        if (request.Targets == null && request.ExpiryBehaviour == PasswordExpiryBehaviour.NeverExpires)
        {
            throw new ArgumentException(
                "expiryBehaviour NeverExpires needs the accounts to be named. A password set on every Connected System configured for Password Synchronisation leaves \"Password never expires\" as each account has it; name the Connected System Objects to choose Never expires for them.");
        }

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
        // systems is a single write rather than ten round trips. A propagated change is not queued where a paused
        // system's account is still owed its first password (#2160): the Initial Password wins there, as at staging,
        // so that system is dropped from what the change reports and the Activity says why.
        var notQueued = changes.Count > 0 ? await _syncRepo.QueuePasswordChangesAsync(changes) : [];
        var notQueuedSystems = notQueued.Select(c => c.ConnectedSystemId).ToHashSet();
        var owedFirstPassword = outcomes.Where(o => notQueuedSystems.Contains(o.ConnectedSystemId)).Select(o => o.ConnectedSystemName).ToList();
        outcomes = outcomes.Where(o => !notQueuedSystems.Contains(o.ConnectedSystemId)).ToList();

        activity.Message = DescribeQueueOutcome(outcomes, request.Origin, owedFirstPassword);
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
    /// <param name="owedFirstPassword">
    /// The Connected Systems a propagated change was not queued for because Password Synchronisation is switched off
    /// there and the account it would reach is still owed its first password, which wins (#2160).
    /// </param>
    internal static string DescribeQueueOutcome(IReadOnlyList<PasswordQueueTargetOutcome> outcomes, PendingPasswordChangeOrigin origin,
        IReadOnlyList<string>? owedFirstPassword = null)
    {
        if (origin == PendingPasswordChangeOrigin.Explicit)
        {
            return $"Password set requested for {outcomes.Count} account{(outcomes.Count == 1 ? string.Empty : "s")}: " +
                   string.Join(", ", outcomes.Select(o => o.ConnectedSystemName)) + ".";
        }

        var notQueued = owedFirstPassword is { Count: > 0 }
            ? $"Not queued for {string.Join(", ", owedFirstPassword)}: Password Synchronisation is switched off there, and the Connected System " +
              "Object this change would reach is still owed its first password, which JIM delivers instead."
            : null;

        if (outcomes.Count == 0)
            return notQueued ?? "No Connected System is configured for Password Synchronisation, so this password was not queued for delivery anywhere.";

        return notQueued == null ? DescribeQueuedPropagation(outcomes) : $"{DescribeQueuedPropagation(outcomes)} {notQueued}";
    }

    /// <summary>
    /// Where a propagated change was queued, and where it is held until Password Synchronisation is switched on.
    /// </summary>
    private static string DescribeQueuedPropagation(IReadOnlyList<PasswordQueueTargetOutcome> outcomes)
    {
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
    /// A bound rather than a page size: a misconfigured target must not turn
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
    /// How long a delivery waits for an export to that account to finish before it tries again (#2173, #2158). Short, because an export batch is seconds rather than minutes, and the wait costs nothing: the
    /// change is not attempted, so no attempt is counted against it.
    /// </summary>
    public static readonly TimeSpan ExportInFlightWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long an export may be Executing before a delivery stops waiting for it (#2173). A Worker that stops part-way
    /// through a batch leaves it Executing until the Worker starts again (a cancelled or failed batch no longer does,
    /// #2083), and a delivery waiting on that would wait until the change expired. An hour is far longer than an export
    /// batch takes, so an export still Executing after it is treated as stranded: the delivery logs it as a warning,
    /// naming the Pending Export, and goes ahead.
    /// </summary>
    public static readonly TimeSpan ExportInFlightCeiling = TimeSpan.FromHours(1);

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

        // Which rows this lane may take (#1635, decision D1; widened from explicit-only by #1697). A system
        // whose Password Synchronisation is unconfigured or switched off delivers no propagated change:
        // requirement 2 has it accumulate rather than discard, so enabling it later has something to drain. An
        // administrator's explicit set and a provisioned account's first password are both delivered there
        // anyway, because the account is already named and there is no configuration decision left to defer to.
        // So the lane excludes only propagated rows over such a system, and claims everything due over a live one.
        var excludePropagated = connectedSystem.PasswordSynchronisation is not { Enabled: true };

        // The retry policy. A system with no configuration at all still delivers explicit sets and provisioned
        // first passwords, under JIM's defaults; the transient instance carries those and is never persisted, so
        // nothing here creates a configuration the administrator did not.
        var configuration = connectedSystem.PasswordSynchronisation
                            ?? new ConnectedSystemPasswordSynchronisation { ConnectedSystemId = connectedSystem.Id };

        // Expiry first, so a change on its way out is not attempted, and its attempt count not inflated, on the
        // very lane that retires it. Expiry touches Pending rows only; a claimed row belongs to its claimant. Over
        // a paused system only the explicit and provisioned rows are retired: the held propagated ones are left
        // exactly as they were, to be expired or delivered by the first lane after the system is switched back on.
        result.ExpiredCount = await _syncRepo.ExpirePasswordChangesAsync(connectedSystem.Id, asOf, excludePropagated);

        var claimed = await _syncRepo.ClaimDuePasswordChangesAsync(connectedSystem.Id, claimedBy, asOf, ClaimLease, ClaimBatchSize, excludePropagated);
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
                    Math.Min(ClaimBatchSize, MaximumChangesPerPass - attemptedInTotal), excludePropagated);
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
            "DeliverDuePasswordChangesAsync: Connected System {ConnectedSystemId}: {Delivered} delivered ({LeftToFlow} leaving the enabled state to an Attribute Flow, " +
            "{NeverExpiresLeftToFlow} leaving \"Password never expires\" to an Attribute Flow, {WithheldEnables} completing an enable withheld until the password, " +
            "{NotHonoured} where the system could not apply everything asked, {FirstPasswords} delivered as an account's first password in place of " +
            "its Initial Password, {PromisesNotApplied} owing a first password whose Initial Password's settings could not be applied), {Retrying} retrying, " +
            "{Parked} parked, {Expired} expired, {Withdrawn} withdrawn, {Postponed} waiting for an export to the account to finish, {Released} released unattempted.",
            connectedSystem.Id, result.DeliveredCount, result.EnabledStateLeftToAttributeFlowCount, result.PasswordNeverExpiresLeftToAttributeFlowCount,
            result.WithheldEnablesCompletedCount, result.ExpiryBehaviourNotHonouredCount, result.FirstPasswordsDeliveredInPlaceCount,
            result.FirstPasswordPromisesNotAppliedCount, result.RetryingCount, result.ParkedCount, result.ExpiredCount,
            result.WithdrawnCount, result.PostponedCount, result.ReleasedCount);

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

        // Read once per batch, and only when the batch actually holds a row owing an account its first password: an
        // Initial Password, which generates its password from the rule, or a change carrying the first password in its
        // place, which takes only the rule's promises about the account (#2160). A lane over a system nothing has
        // provisioned into recently must not pay for a query it will never use. The rule's settings and the discovered
        // policy are read fresh on every batch, never cached across a lane's own claims, so a configuration change takes
        // effect on the very next attempt rather than waiting for the lane to restart (#1697). The policy and the
        // resolver serve only an Initial Password, which is the one that generates.
        Dictionary<int, SyncRuleInitialPassword>? initialPasswordConfigurations = null;
        ConnectedSystemPasswordPolicy? discoveredPolicy = null;
        InitialPasswordResolver? initialPasswordResolver = null;
        if (claimed.Any(c => c.OwesInitialPassword))
        {
            var syncRuleIds = claimed
                .Where(c => c.OwesInitialPassword && c.SyncRuleId.HasValue)
                .Select(c => c.SyncRuleId!.Value)
                .Distinct()
                .ToList();
            initialPasswordConfigurations = await _syncRepo.GetInitialPasswordConfigurationsAsync(syncRuleIds);
        }

        if (claimed.Any(c => c.IsProvisioned))
        {
            discoveredPolicy = await _syncRepo.GetDiscoveredPasswordPolicyAsync(connectedSystem.Id);
            initialPasswordResolver = new InitialPasswordResolver(_passwordGenerator, _credentialProtection());
        }

        // Read at most once per batch, and only once an administrator's reset has actually changed "Password never
        // expires" on an account (#2159), which is rare: what is wanted is whether an export Synchronisation Rule will put
        // the flow's value back, so the Activity can say so.
        var exportRules = new Lazy<Task<List<SyncRule>>>(() => _syncRepo.GetSyncRulesAsync(connectedSystem.Id, includeDisabled: false));

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

                var attempt = await DeliverOneAsync(passwordConnector, connectedSystem, change, configuration,
                    initialPasswordConfigurations, discoveredPolicy, initialPasswordResolver, exportRules, asOf, cancellationToken);

                switch (attempt.Disposition)
                {
                    case PasswordDeliveryDisposition.Delivered:
                        delivered.Add(change.Id);
                        result.DeliveredCount++;
                        if (attempt.Enablement == AccountFlagBasis.LeftToAttributeFlow)
                            result.EnabledStateLeftToAttributeFlowCount++;
                        else if (attempt.Enablement == AccountFlagBasis.CompletesWithheldEnable)
                            result.WithheldEnablesCompletedCount++;
                        if (attempt.PasswordNeverExpires == AccountFlagBasis.LeftToAttributeFlow)
                            result.PasswordNeverExpiresLeftToAttributeFlowCount++;
                        if (!attempt.ExpiryBehaviourHonoured)
                            result.ExpiryBehaviourNotHonouredCount++;
                        if (attempt.StandIn == InitialPasswordStandIn.Applies)
                            result.FirstPasswordsDeliveredInPlaceCount++;
                        else if (attempt.StandIn is InitialPasswordStandIn.NoLongerSet or InitialPasswordStandIn.OtherAccount)
                            result.FirstPasswordPromisesNotAppliedCount++;
                        break;

                    case PasswordDeliveryDisposition.Withdrawn:
                        // Removed exactly as a delivered row is: nothing was sent, but there is nothing left to
                        // send either, and the child Activity already recorded why.
                        delivered.Add(change.Id);
                        result.WithdrawnCount++;
                        break;

                    case PasswordDeliveryDisposition.Postponed:
                        // Written back through the attempt write, which is guarded on the claim, though nothing was
                        // attempted: the row carries its new retry time and an unchanged attempt count (#2173).
                        attempted.Add(change);
                        result.PostponedCount++;
                        break;

                    default:
                        attempted.Add(change);
                        if (change.Status == PendingPasswordChangeStatus.Parked)
                            result.ParkedCount++;
                        else
                            result.RetryingCount++;
                        break;
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
    /// Delivers one queued change, recording its outcome on the change. Returns what happened to the row: whether
    /// it was delivered, kept for another attempt or for a person to look at, or withdrawn because there is
    /// nothing left to deliver; and, for a delivery, which rung decided each of the account's flags (#2064, #2159).
    /// </summary>
    private async Task<PasswordDeliveryAttempt> DeliverOneAsync(
        IConnectorPasswordManagement passwordConnector,
        ConnectedSystem connectedSystem,
        PendingPasswordChange change,
        ConnectedSystemPasswordSynchronisation configuration,
        Dictionary<int, SyncRuleInitialPassword>? initialPasswordConfigurations,
        ConnectedSystemPasswordPolicy? discoveredPolicy,
        InitialPasswordResolver? initialPasswordResolver,
        Lazy<Task<List<SyncRule>>> exportRules,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        // The account is resolved on every attempt rather than trusted from when the change was queued, which is
        // what lets a propagated change queued before provisioning deliver once the account appears, and what
        // stops any change being sent to an account that has since been deleted and replaced.
        var accounts = await _connectedSystemRepo().GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId);

        if (change.IsProvisioned)
        {
            // Never null here: DeliverBatchAsync builds both whenever the claimed batch holds a Provisioned row,
            // and this change is one of them.
            return await DeliverProvisionedAsync(passwordConnector, connectedSystem, change, accounts,
                initialPasswordConfigurations!, discoveredPolicy, initialPasswordResolver!, configuration, asOf, cancellationToken);
        }

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
                return PasswordDeliveryAttempt.Kept;
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
                return PasswordDeliveryAttempt.Kept;
            }

            change.ConnectedSystemObjectId = account.Id;
        }

        if (change.EncryptedPassword == null)
        {
            // Defensive: an Explicit or Propagated row always carries a password through every path that queues
            // one (SetPasswordAsync, and Supersede when a newer change replaces an older row); only a Provisioned
            // row, handled above, is ever queued without one. If this is ever reached there is nothing to send,
            // and no number of attempts changes that.
            Log.Error(
                "DeliverDuePasswordChangesAsync: A {Origin} password change for Connected System {ConnectedSystemId} carries no password. This should not be possible.",
                change.Origin, connectedSystem.Id);
            change.RecordAttempt(PasswordSetFailureReason.ConfigurationFault,
                "This password change carries no password to send.", configuration, asOf);
            change.Status = PendingPasswordChangeStatus.Parked;
            change.NextRetryAt = null;
            await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
            return PasswordDeliveryAttempt.Kept;
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
            return PasswordDeliveryAttempt.Kept;
        }

        // Only an administrator who named the account decides to enable or disable it, or whether its password never
        // expires. A propagated password reaches accounts an administrator may have disabled, or exempted from expiry,
        // on purpose, and changing either would undo that silently, as a side effect of somebody changing their
        // password elsewhere. The enables any delivery completes are the one JIM withheld until a password existed
        // (#2039), because the Attribute Flow asked for it (#2064), and the one the provisioning rule promised an account
        // this change is the first password of, in place of its Initial Password (#2160).
        var (standIn, firstPassword) = ResolveFirstPassword(connectedSystem, change, account, initialPasswordConfigurations);
        var (flags, facts) = await DecideAccountFlagsAsync(connectedSystem, change, account, firstPassword, change.ExpiryBehaviour, asOf);
        if (flags == null)
            return await KeepUndecidedAsync(connectedSystem, change, configuration, asOf);
        if (facts.ExportInFlight && AccountFlagsPolicy.WaitsForAnExportInFlight(flags))
            return PostponeForExportInFlight(connectedSystem, change, account, asOf);

        // The account is being created right now (#2160). Its entry can exist part-way through the Create's batch, and
        // the export stages the account's Initial Password only once the batch has come back: a change delivered and
        // removed in between would let that Initial Password be queued over the person's password. A change owing no
        // first password waits, counting nothing; staging then makes it the account's first password.
        if (facts.CreateInFlight && !change.OwesInitialPassword)
            return PostponeForExportInFlight(connectedSystem, change, account, asOf);

        // "Must change at next sign-in" belongs to the password being delivered; only where the decision writes
        // "Password never expires" does the expiry sent follow it (#2159, #2160).
        var expiryBehaviour = AccountFlagsPolicy.ExpiryToSend(change.ExpiryBehaviour, flags);

        // A Connector that throws rather than classifying comes back from the core as a transient failure, so the
        // change is kept and one target's fault never stops the lane reaching the others.
        var setResult = await PasswordDeliveryCore.SetPasswordAsync(passwordConnector, account, password,
            BuildPasswordSetOptions(expiryBehaviour, flags), cancellationToken);

        if (!setResult.Success)
        {
            change.RecordAttempt(setResult.FailureReason, setResult.ErrorMessage, configuration, asOf);
            await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
            return PasswordDeliveryAttempt.Kept;
        }

        var putBack = await DescribeFlagPutBackAsync(connectedSystem, account, flags, setResult, exportRules);
        return await RecordDeliveredAsync(connectedSystem, change, flags, expiryBehaviour, setResult, password,
            staticInitialPassword: false, putBack, standIn);
    }

    /// <summary>
    /// Whether this delivery is an account's first password in place of its Initial Password (#2160), and if so the
    /// provisioning Synchronisation Rule's promises about the account, read fresh for this batch. Only for the account
    /// the change owes its first password: a propagated change re-resolves its account on every attempt, and promises
    /// made for one account are never applied to another. A rule that no longer sets an Initial Password takes its
    /// promises with it; the change is still the person's password, so it is delivered, never withdrawn.
    /// </summary>
    private static (InitialPasswordStandIn StandIn, InitialPasswordAccountFlags? FirstPassword) ResolveFirstPassword(
        ConnectedSystem connectedSystem,
        PendingPasswordChange change,
        ConnectedSystemObject account,
        Dictionary<int, SyncRuleInitialPassword>? initialPasswordConfigurations)
    {
        if (!change.CarriesInitialPassword)
            return (InitialPasswordStandIn.None, null);

        if (change.ProvisionedConnectedSystemObjectId != account.Id)
        {
            Log.Warning("DeliverDuePasswordChangesAsync: Password change {PendingPasswordChangeId} on Connected System {ConnectedSystemId} owes account " +
                "{OwedConnectedSystemObjectId} its first password but reached account {ConnectedSystemObjectId}, the person's account there now; the Initial " +
                "Password's settings are not applied to it",
                change.Id, connectedSystem.Id, change.ProvisionedConnectedSystemObjectId, account.Id);
            return (InitialPasswordStandIn.OtherAccount, null);
        }

        var settings = change.SyncRuleId is { } syncRuleId && initialPasswordConfigurations != null
                       && initialPasswordConfigurations.TryGetValue(syncRuleId, out var found)
            ? found
            : null;

        return settings is { Enabled: true }
            ? (InitialPasswordStandIn.Applies, new InitialPasswordAccountFlags(settings.EnableAccount, settings.ExpiryBehaviour))
            : (InitialPasswordStandIn.NoLongerSet, null);
    }

    /// <summary>
    /// What the Connector is asked to apply: the expiry behaviour as asked, the enabled state as decided, and whether to
    /// leave "Password never expires" alone, which is wherever the decision does not write it (#2159).
    /// </summary>
    private static PasswordSetOptions BuildPasswordSetOptions(PasswordExpiryBehaviour expiryBehaviour, AccountFlagsDecision flags) => new()
    {
        ExpiryBehaviour = expiryBehaviour,
        EnableAccount = flags.EnableAccount,
        LeavePasswordNeverExpiresAsIs = flags.PasswordNeverExpires == null
    };

    /// <summary>
    /// Why an administrator's reset that changed "Password never expires" will not last (#2159), or null where nothing
    /// will put the flag back. Two things do: the export JIM held until the account had a password (#2039), which still
    /// carries the Attribute Flow's userAccountControl and writes it once released; and an export Synchronisation Rule
    /// that writes userAccountControl with Enforce State on, which puts its value back at the next synchronisation.
    /// Either way a change at the next sign-in may stop being asked for, which matters for a helpdesk reset.
    /// </summary>
    private static async Task<string?> DescribeFlagPutBackAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemObject account,
        AccountFlagsDecision flags,
        PasswordSetResult setResult,
        Lazy<Task<List<SyncRule>>> exportRules)
    {
        if (flags.PasswordNeverExpiresBasis != AccountFlagBasis.AdministratorChoice || !setResult.PasswordNeverExpiresChanged)
            return null;

        if (flags.EnablementBasis == AccountFlagBasis.CompletesWithheldEnable)
            return "\"Password never expires\" was changed on this account, but the export JIM held until the account had a password still carries " +
                   "the Attribute Flow's userAccountControl, and writes it back, \"Password never expires\" and all, once it is released.";

        List<SyncRule> rules;
        try
        {
            rules = await exportRules.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Advisory only: the password is set and the outcome is recorded either way. Logged rather than swallowed, so
            // a warning the Activity could not carry is still visible.
            Log.Warning(ex, "DeliverDuePasswordChangesAsync: Could not read the export Synchronisation Rules of Connected System {ConnectedSystemId} " +
                "to tell whether one puts back the \"Password never expires\" just changed on account {ConnectedSystemObjectId}",
                connectedSystem.Id, account.Id);
            return null;
        }

        var enforcing = rules.FirstOrDefault(rule => AccountControlAttributeFlows.EnforcesAccountControl(rule, account.TypeId));
        return enforcing == null
            ? null
            : $"\"Password never expires\" was changed on this account, but the Synchronisation Rule {enforcing.Name} writes its userAccountControl " +
              "with Enforce State on, so the next synchronisation puts the Attribute Flow's value back, and with it whether the password never " +
              "expires; while that value keeps \"Password never expires\" set, Active Directory does not ask for a change at the next sign-in.";
    }

    /// <summary>
    /// Delivers a Provisioned row's first password (#1697): resolves the account and the provisioning
    /// Synchronisation Rule's settings fresh on every attempt, generates or decrypts the password from those
    /// settings, and sends it. Withdraws the row rather than delivering or retrying wherever there is nothing
    /// left to deliver, so a stale row never sits parked or retrying against work nobody needs done any more.
    /// </summary>
    private async Task<PasswordDeliveryAttempt> DeliverProvisionedAsync(
        IConnectorPasswordManagement passwordConnector,
        ConnectedSystem connectedSystem,
        PendingPasswordChange change,
        List<ConnectedSystemObject> accounts,
        Dictionary<int, SyncRuleInitialPassword> initialPasswordConfigurations,
        ConnectedSystemPasswordPolicy? discoveredPolicy,
        InitialPasswordResolver initialPasswordResolver,
        ConnectedSystemPasswordSynchronisation configuration,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        // The export that staged this row always named the account it provisioned, so the row is re-read among
        // the person's current accounts rather than trusted by id alone: an account deleted since (the foreign
        // key nulls the row's reference) or disjoined from the person is not written to.
        var account = change.ConnectedSystemObjectId is { } accountId
            ? accounts.SingleOrDefault(a => a.Id == accountId && a.ConnectedSystemId == connectedSystem.Id)
            : null;

        if (account == null)
        {
            await RecordWithdrawnOutcomeActivityAsync(connectedSystem, change,
                $"Initial password no longer needed: the account it was for no longer exists in {connectedSystem.Name}, or is no longer joined to this person.");
            return PasswordDeliveryAttempt.Withdrawn;
        }

        // Read fresh rather than trusted from when the row was staged, so a Synchronisation Rule switched off or
        // reconfigured since takes effect on this very attempt.
        var initialPasswordConfiguration = change.SyncRuleId is { } syncRuleId
                                            && initialPasswordConfigurations.TryGetValue(syncRuleId, out var found)
            ? found
            : null;

        if (initialPasswordConfiguration is not { Enabled: true })
        {
            await RecordWithdrawnOutcomeActivityAsync(connectedSystem, change,
                "Initial password no longer needed: the Synchronisation Rule that provisioned this account no longer sets one, or has been deleted.");
            return PasswordDeliveryAttempt.Withdrawn;
        }

        var resolution = initialPasswordResolver.Resolve(initialPasswordConfiguration, discoveredPolicy);
        if (!resolution.IsUsable)
        {
            // A refusal here is always a configuration problem, never a target-side one, so it parks exactly as
            // an undecryptable stored password does: no number of attempts fixes it without a person acting.
            change.RecordAttempt(resolution.FailureReason ?? PasswordSetFailureReason.ConfigurationFault,
                resolution.Message, configuration, asOf);
            change.Status = PendingPasswordChangeStatus.Parked;
            change.NextRetryAt = null;
            await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
            return PasswordDeliveryAttempt.Kept;
        }

        // Where an export wrote the account's flags, the Attribute Flow decides both the enabled state and "Password never
        // expires", and the rule's Enable Account and expiry do not apply to them (#2064, #2159); an enable JIM withheld
        // until a password existed is completed (#2039). The rule's own settings, never the row's placeholder values: a
        // Provisioned row carries no expiry behaviour or enable decision of its own, because both belong to the
        // Synchronisation Rule that provisioned the account and can change independently of the queued row.
        var expiryBehaviour = initialPasswordConfiguration.ExpiryBehaviour;
        var (flags, facts) = await DecideAccountFlagsAsync(connectedSystem, change, account,
            new InitialPasswordAccountFlags(initialPasswordConfiguration.EnableAccount, expiryBehaviour), expiryBehaviour, asOf);
        if (flags == null)
            return await KeepUndecidedAsync(connectedSystem, change, configuration, asOf);
        if (facts.ExportInFlight && AccountFlagsPolicy.WaitsForAnExportInFlight(flags))
            return PostponeForExportInFlight(connectedSystem, change, account, asOf);

        // A Connector that throws rather than classifying comes back from the core as a transient failure, so the
        // change is kept and one target's fault never stops the lane reaching the others.
        var setResult = await PasswordDeliveryCore.SetPasswordAsync(passwordConnector, account, resolution.Password!,
            BuildPasswordSetOptions(expiryBehaviour, flags), cancellationToken);

        if (!setResult.Success)
        {
            change.RecordAttempt(setResult.FailureReason, setResult.ErrorMessage, configuration, asOf);
            await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
            return PasswordDeliveryAttempt.Kept;
        }

        return await RecordDeliveredAsync(connectedSystem, change, flags, expiryBehaviour, setResult, resolution.Password!,
            staticInitialPassword: initialPasswordConfiguration.Source == InitialPasswordSource.Static, putBack: null);
    }

    /// <summary>
    /// Decides what delivering this change may write to the account's flags, its enabled state (#2064) and "Password
    /// never expires" (#2159), and says whether an export to the account is in flight (#2173). The facts the decision turns on are read afresh at the attempt, not taken from
    /// the claimed row: whether the account holds an enable JIM withheld until a password existed (#2039), and whether
    /// an export has written the account's flags. An export can mark a claimed row, a leaver disabled moments
    /// ago, while the lane works through the accounts ahead of it; and one still being sent has recorded neither its mark
    /// nor any hold it placed yet, which is why the caller waits for it. Returns a null decision when those facts cannot be read:
    /// the caller keeps the change rather than guess, because guessing "not held" could leave an account disabled the
    /// flow asks to be enabled, and guessing "not marked" could enable one the flow has disabled.
    /// </summary>
    private async Task<(AccountFlagsDecision? Decision, AccountEnablementFacts Facts)> DecideAccountFlagsAsync(
        ConnectedSystem connectedSystem,
        PendingPasswordChange change,
        ConnectedSystemObject account,
        InitialPasswordAccountFlags? firstPassword,
        PasswordExpiryBehaviour requestedExpiry,
        DateTime asOf)
    {
        // Read for every attempt (#2160): besides the hold and the mark the decision turns on, whether the account's own
        // Create is in flight decides whether a change owing no first password may go ahead, and that holds even for an
        // administrator's decision. One small query, through indexes, per attempt.
        AccountEnablementFacts facts;
        try
        {
            facts = await _syncRepo.GetAccountEnablementFactsAsync(connectedSystem.Id, change.Id, account.Id, asOf - ExportInFlightCeiling);
            if (facts.StrandedExportId is { } strandedExportId)
                Log.Warning("DeliverDuePasswordChangesAsync: Pending Export {PendingExportId} to account {ConnectedSystemObjectId} on Connected System {ConnectedSystemId} " +
                    "has been Executing for more than {Ceiling}, far longer than an export batch takes, so it was most likely left there by a run that failed or was cancelled. " +
                    "Password change {PendingPasswordChangeId} no longer waits for it. The Worker recovers stranded exports when it restarts",
                    strandedExportId, account.Id, connectedSystem.Id, ExportInFlightCeiling, change.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "DeliverDuePasswordChangesAsync: Could not read whether account {ConnectedSystemObjectId} on Connected System {ConnectedSystemId} " +
                "holds an enable awaiting its password, or has had its enabled state written by an export; the change is kept and tried again",
                account.Id, connectedSystem.Id);
            return (null, new AccountEnablementFacts(false, false, false, null));
        }

        // An administrator's choice is not overridden by a withheld enable, so the hold is not read for one. The mark only
        // ever moves from false to true, so the claimed row's own value is kept where it is already set.
        var holdsWithheldEnable = AccountFlagsPolicy.DependsOnWithheldEnable(change.Origin, change.EnableAccount) && facts.HoldsWithheldEnable;
        var accountFlagsWrittenByExport = change.EnabledStateSetByAttributeFlow || facts.EnabledStateSetByAttributeFlow;

        var decision = AccountFlagsPolicy.Decide(change.Origin, change.EnableAccount, requestedExpiry, accountFlagsWrittenByExport,
            firstPassword, holdsWithheldEnable);
        return (decision, facts);
    }

    /// <summary>
    /// Gives the change back unattempted because an export is being sent to the account right now
    /// (<see cref="AccountFlagsPolicy.WaitsForAnExportInFlight"/>). That export may be disabling the account (a leaver,
    /// say, #2173), writing the flags as the Attribute Flow that owns them (#2159), or holding back an enable this
    /// delivery would complete (#2158), and records any of these only once its batch has come back, so the delivery waits
    /// for it rather than decide without it. Nothing was sent and nothing failed, so no attempt is counted and no Activity
    /// is recorded; the lane's summary counts the wait.
    /// </summary>
    private static PasswordDeliveryAttempt PostponeForExportInFlight(
        ConnectedSystem connectedSystem,
        PendingPasswordChange change,
        ConnectedSystemObject account,
        DateTime asOf)
    {
        change.Postpone(asOf + ExportInFlightWait);
        Log.Debug("DeliverDuePasswordChangesAsync: An export to account {ConnectedSystemObjectId} on Connected System {ConnectedSystemId} is in flight, " +
            "and may change what delivering password change {PendingPasswordChangeId} does to the account's flags; it waits until {NextRetryAt}",
            account.Id, connectedSystem.Id, change.Id, change.NextRetryAt);
        return PasswordDeliveryAttempt.Postponed;
    }

    /// <summary>
    /// Keeps a change whose effect on the account's enabled state could not be decided, as a transient failure:
    /// nothing was sent, and the next attempt decides afresh.
    /// </summary>
    private async Task<PasswordDeliveryAttempt> KeepUndecidedAsync(
        ConnectedSystem connectedSystem,
        PendingPasswordChange change,
        ConnectedSystemPasswordSynchronisation configuration,
        DateTime asOf)
    {
        change.RecordAttempt(PasswordSetFailureReason.Transient,
            "JIM could not read whether this account's flags are set by an Attribute Flow, or its enable is waiting for its password, so nothing was sent.",
            configuration, asOf);
        await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: false);
        return PasswordDeliveryAttempt.Kept;
    }

    /// <summary>
    /// Records what one Connected System did with a queued password change, as a child of the Activity for the
    /// change itself (requirement 23).
    /// <para>
    /// The child carries the outcome and the target's own words on a refusal, and never the password: the queue
    /// row is deleted on success, so this Activity is all that survives to say the password reached this system.
    /// </para>
    /// </summary>
    /// <param name="note">For a delivery, information about how the account's flags came out, added to the message.</param>
    /// <param name="caveat">For a delivery, what did not go as asked, which completes the Activity with a warning.</param>
    private async Task RecordDeliveryOutcomeActivityAsync(ConnectedSystem connectedSystem, PendingPasswordChange change, bool success,
        string? delivered = null, string? note = null, string? caveat = null)
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
                ? (delivered ?? $"Password set on {connectedSystem.Name}.") + (note == null ? string.Empty : $" {note}") + (caveat == null ? string.Empty : $" {caveat}")
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
        // A delivery carrying a caveat set the password, so it is no failure, but it did not do everything asked of it
        // (#2159). Completed with a warning so the difference reaches everything that reads outcomes by status, which
        // is how the portal, the REST API and PowerShell decide whether to say "Set" plainly.
        if (!success)
            await _completeActivityWithError(activity, failure!);
        else if (caveat != null)
            await _completeActivityWithWarning(activity);
        else
            await _completeActivity(activity);
    }

    /// <summary>
    /// Records a delivery that set the password, completed plainly where the Connected System applied everything asked
    /// of it and with a warning where it did not, or where what it applied will not last (#2159), and says which it was.
    /// </summary>
    /// <param name="requestedExpiry">The expiry behaviour the delivery asked for, to compare with what the account now does.</param>
    /// <param name="password">
    /// The password just sent, in scope only so the Connector's warning can be checked for it before it is recorded.
    /// Never recorded or logged itself.
    /// </param>
    /// <param name="staticInitialPassword">Whether the password is a Synchronisation Rule's one Static Initial Password.</param>
    /// <param name="putBack">Why an administrator's change to "Password never expires" will not last; null where it will.</param>
    /// <param name="standIn">Whether the password was delivered as an account's first password in place of its Initial Password (#2160).</param>
    private async Task<PasswordDeliveryAttempt> RecordDeliveredAsync(ConnectedSystem connectedSystem, PendingPasswordChange change,
        AccountFlagsDecision flags, PasswordExpiryBehaviour requestedExpiry, PasswordSetResult setResult, string password,
        bool staticInitialPassword, string? putBack, InitialPasswordStandIn standIn = InitialPasswordStandIn.None)
    {
        var delivered = DescribeDelivery(connectedSystem, change, flags, setResult, standIn);
        var note = DescribePasswordNeverExpires(connectedSystem, flags, requestedExpiry, setResult);
        var caveat = JoinSentences(DescribeCaveat(connectedSystem, flags, setResult, password, staticInitialPassword), putBack);
        await RecordDeliveryOutcomeActivityAsync(connectedSystem, change, success: true, delivered, note, caveat);
        return PasswordDeliveryAttempt.Delivered(flags, expiryBehaviourHonoured: setResult.ExpiryBehaviourHonoured, standIn);
    }

    private static string? JoinSentences(string? first, string? second) =>
        first == null ? second : second == null ? first : $"{first} {second}";

    /// <summary>
    /// What a Connector that set the password could not do of what was asked (#2159), for the delivery's Activity:
    /// what was applied instead, then the Connector's own words, then, where "Password never expires" left to its owner
    /// is why, who owns it and what that means. Null where it did everything asked.
    /// <para>
    /// The Connector's words go onto an Activity that the portal, the REST API and PowerShell all show, and a
    /// Connector is under no obligation to keep the password out of them. Withheld whole rather than patched where
    /// they contain it, for the reason the LDAP Connector withholds a directory's message: cutting the value out
    /// depends on the echo matching exactly.
    /// </para>
    /// </summary>
    private static string? DescribeCaveat(ConnectedSystem connectedSystem, AccountFlagsDecision flags, PasswordSetResult setResult,
        string password, bool staticInitialPassword)
    {
        if (setResult.ExpiryBehaviourHonoured)
            return null;

        var applied = setResult.AppliedExpiryBehaviour switch
        {
            PasswordExpiryBehaviour.RequireChangeAtNextSignIn => ": a change is required at the next sign-in",
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy => $": the password expires according to {connectedSystem.Name}'s own password policy",
            PasswordExpiryBehaviour.NeverExpires => ": the password never expires",
            _ => string.Empty
        };

        var warning = setResult.ExpiryBehaviourWarning?.Trim();
        if (!string.IsNullOrEmpty(warning) && !string.IsNullOrEmpty(password) && warning.Contains(password, StringComparison.OrdinalIgnoreCase))
            warning = "[the Connected System's warning is withheld because it contained the password]";

        var caveat = string.IsNullOrEmpty(warning)
            ? $"Not everything asked for could be applied{applied}."
            : $"Not everything asked for could be applied{applied}. {warning}";

        // A change at the next sign-in defeated by "Password never expires" left set: say who keeps the flag, and, for a
        // Static Initial Password, the consequence that makes it matter most.
        if (flags.PasswordNeverExpires != null || setResult.AppliedExpiryBehaviour != PasswordExpiryBehaviour.NeverExpires)
            return caveat;

        var owner = flags.PasswordNeverExpiresBasis == AccountFlagBasis.LeftToAttributeFlow
            ? "\"Password never expires\" was left to the export that writes userAccountControl, which keeps it set; set it in that Attribute Flow " +
              "only where passwords should never expire, or choose another expiry for the Initial Password."
            : "A password set on every Connected System leaves \"Password never expires\" as each account has it.";
        var shared = staticInitialPassword
            ? " Every account given this Synchronisation Rule's Static Initial Password therefore keeps that one shared password, which never expires."
            : string.Empty;
        return $"{caveat} {owner}{shared}";
    }

    /// <summary>
    /// Says how "Password never expires" came out where the delivery left it to its owner and that differs from the expiry
    /// asked for (#2159), as information rather than a warning: nothing failed, the flag simply belongs to something else.
    /// Null where the delivery wrote the flag itself, where nothing differs, and where the difference is already a warning.
    /// The wording names the export that writes userAccountControl rather than an Attribute Flow, because the mark that
    /// decided it is also set by a Delete performed as a disable.
    /// </summary>
    private static string? DescribePasswordNeverExpires(ConnectedSystem connectedSystem, AccountFlagsDecision flags,
        PasswordExpiryBehaviour requestedExpiry, PasswordSetResult setResult)
    {
        if (flags.PasswordNeverExpires != null || !setResult.ExpiryBehaviourHonoured || setResult.AppliedExpiryBehaviour is not { } applied)
            return null;

        var askedNeverExpires = requestedExpiry == PasswordExpiryBehaviour.NeverExpires;
        var neverExpires = applied == PasswordExpiryBehaviour.NeverExpires;
        if (askedNeverExpires == neverExpires)
            return null;

        var leftToExport = flags.PasswordNeverExpiresBasis == AccountFlagBasis.LeftToAttributeFlow;
        if (neverExpires)
            return leftToExport
                ? "\"Password never expires\" was left to the export that writes userAccountControl, which sets it, so the password never expires."
                : "\"Password never expires\" was left as the account has it, which is set, so the password never expires.";

        return leftToExport
            ? "\"Password never expires\" was left to the export that writes userAccountControl, which leaves it off, so the password expires " +
              $"according to {connectedSystem.Name}'s own password policy. To have these passwords never expire, set Password never expires " +
              "(65536) in the Attribute Flow that writes userAccountControl."
            : "A password set on every Connected System leaves \"Password never expires\" as each account has it, so the Never expires asked for " +
              $"was not applied; the password expires according to {connectedSystem.Name}'s own password policy.";
    }

    /// <summary>
    /// Records that a Provisioned row was removed from the queue without a password ever being sent (#1697),
    /// as a completed child Activity carrying why. Completed rather than failed: nothing went wrong, the
    /// work simply stopped being needed, either because the account it was for is gone or because the
    /// Synchronisation Rule that provisioned it no longer sets one.
    /// </summary>
    private async Task RecordWithdrawnOutcomeActivityAsync(ConnectedSystem connectedSystem, PendingPasswordChange change, string message)
    {
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.PasswordSynchronisation,
            TargetOperationType = ActivityTargetOperationType.SetPassword,
            TargetContext = connectedSystem.Name,
            ParentActivityId = change.ActivityId,
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystemObjectId = change.ConnectedSystemObjectId,
            MetaverseObjectId = change.MetaverseObjectId,
            Message = message
        };

        await _createSystemActivity(activity);
        await _completeActivity(activity);
    }

    /// <summary>
    /// What a delivery did, saying why the account's enabled state is what it is wherever the precedence (#2064) did
    /// something other than what the setting alone would suggest: an administrator reading why a new starter's account
    /// is still disabled, or was enabled despite Enable Account being off, finds the answer here. A password delivered as
    /// an account's first password in place of its Initial Password (#2160) says so, and how the Initial Password's
    /// promises about the account came out. An enable is claimed only where the Connected System applied everything
    /// asked of it: a directory that cannot enable an account says so on the warning instead.
    /// </summary>
    private static string DescribeDelivery(ConnectedSystem connectedSystem, PendingPasswordChange change, AccountFlagsDecision flags,
        PasswordSetResult setResult, InitialPasswordStandIn standIn)
    {
        var system = connectedSystem.Name;
        var set = standIn switch
        {
            _ when change.IsProvisioned => $"Initial password set on {system}",
            InitialPasswordStandIn.Applies => $"Password set on {system} as this Connected System Object's first password, in place of its Initial Password",
            InitialPasswordStandIn.NoLongerSet => $"Password set on {system} as this Connected System Object's first password. The Synchronisation Rule " +
                                                  "that provisioned it no longer sets an Initial Password, or has been deleted, so its settings for enabling " +
                                                  "the Connected System Object and for whether its password never expires were not applied",
            InitialPasswordStandIn.OtherAccount => $"Password set on {system}. It was also to be the first password of a Connected System Object there " +
                                                   "that is no longer this person's, so that Initial Password's settings for enabling the Connected System " +
                                                   "Object and for whether its password never expires were not applied to this one",
            _ => $"Password set on {system}"
        };

        var enabled = flags.EnableAccount == true && setResult.ExpiryBehaviourHonoured;
        return flags.EnablementBasis switch
        {
            AccountFlagBasis.LeftToAttributeFlow =>
                $"{set}. The account's enabled state was left to its Attribute Flow, which sets it, so the Initial Password's \"Enable the Connected System Object once the password is set\" did not apply.",
            AccountFlagBasis.CompletesWithheldEnable =>
                $"{set}, and the account enabled as its Attribute Flow asks: JIM held the enable back until the account had a password.",
            AccountFlagBasis.SyncRuleSetting when standIn == InitialPasswordStandIn.Applies && enabled =>
                $"{set}, and the Connected System Object enabled as the Synchronisation Rule's Initial Password asks.",
            AccountFlagBasis.SyncRuleSetting when standIn == InitialPasswordStandIn.Applies && flags.EnableAccount == true =>
                $"{set}. The Synchronisation Rule's Initial Password asks for the Connected System Object to be enabled.",
            AccountFlagBasis.SyncRuleSetting when standIn == InitialPasswordStandIn.Applies =>
                $"{set}. The Synchronisation Rule's Initial Password does not enable it, so it was left as it was created.",
            _ => $"{set}."
        };
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
            "DeliverDueAsync: {Visited} Connected System(s) visited: {Delivered} delivered, {Retrying} retrying, {Parked} parked, {Expired} expired, {Withdrawn} withdrawn, " +
            "{Postponed} waiting for an export to the account to finish, {Problems} problem(s).",
            result.ConnectedSystemsVisited, result.DeliveredCount, result.RetryingCount, result.ParkedCount,
            result.ExpiredCount, result.WithdrawnCount, result.PostponedCount, result.Problems.Count);

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
            // a propagated change is ever held, and not one carrying an account's first password; an explicit set and
            // a provisioned first password are both delivered on a paused system (decision D1; widened by #1697, #2160).
            _ when !systemEnabled && row.IsHeldWhilePasswordSynchronisationIsOff => PasswordChangeTargetState.Held,
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
            AttemptCount = attemptCount,
            // The password is set either way; the warning says the system could not apply everything asked of it,
            // and the Activity's Message above says what it applied instead (#2159).
            CompletedWithWarning = set && newest.CompletedWithWarning
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
