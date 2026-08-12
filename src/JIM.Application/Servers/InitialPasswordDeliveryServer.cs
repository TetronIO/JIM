// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
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
/// <b>No password value leaves this class, or is written anywhere.</b> A generated password is produced at the
/// moment of delivery, handed to the Connector and dropped; a rule set to use one static password decrypts it
/// here and drops it the same way. What is recorded is that a password was owed, how many times JIM has tried,
/// and what the target said when it refused.
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
    private readonly IPasswordGeneratorService _passwordGenerator;
    private readonly Func<ICredentialProtectionService> _credentialProtection;

    /// <param name="credentialProtection">
    /// How to reach credential protection, resolved when a pass runs rather than now. The hosts set
    /// <see cref="JimApplication.CredentialProtection"/> after constructing the facade, so anything captured here
    /// would capture the null that precedes it.
    /// </param>
    internal InitialPasswordDeliveryServer(
        ISyncRepository syncRepository,
        IPasswordGeneratorService passwordGenerator,
        Func<ICredentialProtectionService> credentialProtection)
    {
        _syncRepo = syncRepository;
        _passwordGenerator = passwordGenerator;
        _credentialProtection = credentialProtection;
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

        // Before anything is fetched, so an account whose time ran out is not attempted one last time on its way
        // out, and so a parked record whose administrator never came stops holding a needs-attention marker over
        // work nobody is going to do.
        result.ExpiredCount = await _syncRepo.ExpireInitialPasswordsAsync(connectedSystem.Id, DateTime.UtcNow);
        if (result.ExpiredCount > 0)
            Log.Warning("DeliverOutstandingAsync: {Count} accounts on {SystemName} were provisioned but never got an initial password " +
                "within its time to live, and have been recorded as expired",
                result.ExpiredCount, LogSanitiser.Sanitise(connectedSystem.Name));

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

        // Built per pass rather than held, because credential protection is only reachable once the host has set
        // it, and built here rather than at the top so a pass with nothing to do never asks for it. The service
        // holds no state, so this costs nothing.
        var deliveryService = new InitialPasswordDeliveryService(_passwordGenerator, _credentialProtection());

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

                var outcome = await deliveryService.DeliverAsync(
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
            "{Retrying} to retry, {Parked} parked for an administrator, {NoLongerApplicable} no longer applicable, {Expired} expired",
            LogSanitiser.Sanitise(connectedSystem.Name), result.AttemptedCount, result.DeliveredCount,
            result.RetryingCount, result.ParkedCount, result.NoLongerApplicableCount, result.ExpiredCount);

        return result;
    }

    /// <summary>
    /// Encrypts the one password an administrator chose for every account a Synchronisation Rule provisions, ready
    /// to be stored on the rule (issue #1273).
    /// <para>
    /// Here rather than at each surface so the portal, the REST API and PowerShell cannot encrypt it three
    /// slightly different ways, and so no surface has to decide what to do when credential protection is not
    /// reachable. The answer to that is never "store the plaintext", and keeping it in one place is what
    /// guarantees it.
    /// </para>
    /// </summary>
    /// <returns>The encrypted value to store. The plaintext is not retained.</returns>
    public string ProtectStaticPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // Protect returns null only for a null or empty input, which the guard above has already ruled out.
        return _credentialProtection().Protect(password)!;
    }

    /// <summary>
    /// What is wrong with a Synchronisation Rule's initial-password settings, in language meant for the
    /// administrator reading it. Empty means there is nothing to fix.
    /// <para>
    /// Asked before the settings are saved rather than left to fail per account: an unsatisfiable configuration
    /// parks every account the rule provisions, and the administrator saving it is the person who can fix it.
    /// </para>
    /// <para>
    /// Here rather than at each surface so that the portal and the REST API cannot disagree about what is
    /// savable. They did: the API refused an unsatisfiable generator configuration while the portal saved it
    /// happily, so the same settings were accepted or rejected depending on which surface you used.
    /// </para>
    /// </summary>
    /// <param name="configuration">
    /// The settings to assess, as they would be saved. Null, or settings that are switched off, have nothing that
    /// could be wrong with them: a rule that will not deliver an initial password cannot fail to deliver one.
    /// </param>
    /// <param name="discoveredPolicy">
    /// The password policy JIM discovered on the target, so that a configuration the target would refuse is
    /// reported as well as one JIM itself cannot satisfy.
    /// </param>
    public IReadOnlyList<string> AssessConfiguration(
        SyncRuleInitialPassword? configuration,
        ConnectedSystemPasswordPolicy? discoveredPolicy)
    {
        if (configuration is not { Enabled: true })
            return [];

        // A stored static password is never decrypted to be looked at again, so the only question left about one
        // is whether it is there. It was assessed against this same target policy when it was set.
        if (configuration.Source == InitialPasswordSource.Static)
            return string.IsNullOrEmpty(configuration.StaticPasswordEncryptedValue)
                ? [StaticPasswordMissingProblem]
                : [];

        var policy = configuration.Source == InitialPasswordSource.Custom
            ? configuration.CustomPolicy
            : _passwordGenerator.DeriveFrom(discoveredPolicy);

        return _passwordGenerator.Assess(policy, discoveredPolicy).Problems;
    }

    /// <summary>
    /// What an administrator is told when a rule is set to use one password for every account and has none. Held
    /// as a constant so the portal and the REST API say the same thing, and so a test can pin the wording that
    /// tells somebody how to get out of it.
    /// </summary>
    public const string StaticPasswordMissingProblem =
        "This Synchronisation Rule is set to use one password for every account it provisions, but no password " +
        "has been set. Set one, or choose a different source.";

    /// <summary>
    /// Sets a Synchronisation Rule's parked accounts retrying, and returns how many were released.
    /// <para>
    /// This is the other half of parking. A policy rejection stops the retry loop because the same generator
    /// configuration produces another password the target refuses for the same reason; the administrator
    /// correcting that configuration is the event that makes another attempt worth making, and this is how that
    /// event reaches the parked work. Without it, parking is a one-way door.
    /// </para>
    /// <para>
    /// Releasing makes the accounts outstanding again rather than delivering to them here. Delivery needs an
    /// open Connector connection, so it belongs to the export pass that already owns one; the released accounts
    /// are picked up by the next run over that Connected System with no backoff to wait out. Nothing is
    /// regenerated or invalidated in the meantime because no password was ever stored: each is generated at the
    /// moment of delivery, so the retry uses the corrected configuration by construction.
    /// </para>
    /// </summary>
    /// <param name="syncRuleId">The Synchronisation Rule whose parked accounts to release.</param>
    public async Task<int> ReleaseParkedForSyncRuleAsync(int syncRuleId)
    {
        var released = await _syncRepo.ReleaseParkedInitialPasswordsAsync(syncRuleId);

        if (released > 0)
            Log.Information("ReleaseParkedForSyncRuleAsync: {Count} accounts parked against Synchronisation Rule {SyncRuleId} " +
                "have been released and will be attempted again on its Connected System's next export run", released, syncRuleId);

        return released;
    }

    /// <summary>
    /// Removes initial-password records that reached a terminal state long enough ago to have had their
    /// retention period, and returns how many were removed.
    /// <para>
    /// Parked and Expired records are kept on purpose, so that an account provisioned without a working password
    /// says so rather than disappearing. Kept for ever, though, they are unbounded growth: one row per account
    /// a misconfigured Synchronisation Rule ever provisioned, in a table nobody is watching. This is the other
    /// end of that decision, and it is deliberately the only thing that removes a record JIM did not resolve.
    /// </para>
    /// <para>
    /// Called from housekeeping alongside the change-history trims, under its own retention period
    /// (<see cref="Constants.SettingKeys.InitialPasswordRetentionPeriod"/>) and the shared cleanup batch size.
    /// </para>
    /// </summary>
    /// <param name="olderThan">The retention cutoff; records last touched before this are eligible.</param>
    /// <param name="maxRecords">The most to remove in one pass.</param>
    public async Task<int> DeleteExpiredWorkRecordsAsync(DateTime olderThan, int maxRecords)
    {
        var deleted = await _syncRepo.DeleteTerminalInitialPasswordsAsync(olderThan, maxRecords);

        if (deleted > 0)
            Log.Information("DeleteExpiredWorkRecordsAsync: Removed {Count} initial-password records that had been " +
                "parked or expired since before {OlderThan}", deleted, olderThan);

        return deleted;
    }

    /// <summary>
    /// How many accounts under each of these Synchronisation Rules are waiting on a person, for the indicator on
    /// the Synchronisation Rules list.
    /// <para>
    /// A rule with nothing outstanding is absent from the result rather than present with zeroes, so a list that
    /// renders nothing for a settled rule can do so without asking twice.
    /// </para>
    /// </summary>
    public async Task<Dictionary<int, InitialPasswordAttention>> GetAttentionBySyncRuleAsync(IReadOnlyCollection<int> syncRuleIds)
    {
        ArgumentNullException.ThrowIfNull(syncRuleIds);

        return await _syncRepo.GetInitialPasswordAttentionBySyncRuleAsync(syncRuleIds);
    }

    /// <summary>
    /// The Connected Systems counterpart of <see cref="GetAttentionBySyncRuleAsync"/>.
    /// </summary>
    public async Task<Dictionary<int, InitialPasswordAttention>> GetAttentionByConnectedSystemAsync(IReadOnlyCollection<int> connectedSystemIds)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemIds);

        return await _syncRepo.GetInitialPasswordAttentionByConnectedSystemAsync(connectedSystemIds);
    }

    /// <summary>
    /// What the target said about the initial passwords parked against a Synchronisation Rule, grouped by reason
    /// and with the biggest group first.
    /// <para>
    /// This is what an administrator acts on: the reason names the setting to change, and changing it is what
    /// releases the accounts. Only parked records are reported, because saving releases only what is parked.
    /// </para>
    /// </summary>
    public async Task<List<InitialPasswordRejection>> GetParkedReasonsAsync(int syncRuleId)
    {
        return await _syncRepo.GetParkedInitialPasswordReasonsAsync(syncRuleId);
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
