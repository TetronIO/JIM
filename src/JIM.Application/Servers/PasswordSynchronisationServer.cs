// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
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
