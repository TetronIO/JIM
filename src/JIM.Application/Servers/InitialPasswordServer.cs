// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Owns initial-password configuration and the Synchronisation Rule-side attention it produces (issue #1121).
/// <para>
/// That is: assessing a rule's initial-password settings before they are saved, protecting the one static
/// password an administrator may set on a rule, releasing a rule's parked provisioned accounts so they are
/// attempted again, and answering the rule surfaces' "does this need a person" and "why is it stuck" reads.
/// Delivering the password itself belongs to the Password Delivery Service (#1697): provisioned accounts are
/// staged onto its queue as <see cref="PendingPasswordChangeOrigin.Provisioned"/> rows at export time, and that
/// service is what attempts, retries and parks them. This class has nothing left to do with delivery.
/// </para>
/// <para>
/// <b>No password value leaves this class, or is written anywhere.</b> A generated password is produced at the
/// moment of delivery, handed to the Connector and dropped; a rule set to use one static password decrypts it
/// only to protect it again for storage. What is recorded elsewhere is that a password was owed, how many times
/// JIM has tried, and what the target said when it refused.
/// </para>
/// </summary>
public class InitialPasswordServer
{
    private readonly ISyncRepository _syncRepo;
    private readonly IPasswordGeneratorService _passwordGenerator;
    private readonly Func<ICredentialProtectionService> _credentialProtection;

    /// <param name="credentialProtection">
    /// How to reach credential protection, resolved when a pass runs rather than now. The hosts set
    /// <see cref="JimApplication.CredentialProtection"/> after constructing the facade, so anything captured here
    /// would capture the null that precedes it.
    /// </param>
    internal InitialPasswordServer(
        ISyncRepository syncRepository,
        IPasswordGeneratorService passwordGenerator,
        Func<ICredentialProtectionService> credentialProtection)
    {
        _syncRepo = syncRepository;
        _passwordGenerator = passwordGenerator;
        _credentialProtection = credentialProtection;
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
    /// Releasing makes the accounts due again rather than delivering to them here (#1697): each is a
    /// <see cref="PendingPasswordChangeOrigin.Provisioned"/> row on the Password Delivery Service's queue, and
    /// the update that releases it fires the queue's own NOTIFY trigger, so the service attempts the released
    /// rows within seconds rather than waiting for the Connected System's next export run. Nothing is
    /// regenerated or invalidated in the meantime because no password was ever stored: each is generated at the
    /// moment of delivery, so the retry uses the corrected configuration by construction.
    /// </para>
    /// </summary>
    /// <param name="syncRuleId">The Synchronisation Rule whose parked accounts to release.</param>
    public async Task<int> ReleaseParkedForSyncRuleAsync(int syncRuleId)
    {
        var released = await _syncRepo.ReleaseParkedProvisionedPasswordChangesAsync(syncRuleId);

        if (released > 0)
            Log.Information("ReleaseParkedForSyncRuleAsync: {Count} accounts parked against Synchronisation Rule {SyncRuleId} " +
                "have been released and will be attempted again by the Password Delivery Service", released, syncRuleId);

        return released;
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

        return await _syncRepo.GetProvisionedPasswordAttentionBySyncRuleAsync(syncRuleIds);
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
        return await _syncRepo.GetParkedProvisionedPasswordReasonsAsync(syncRuleId);
    }
}
