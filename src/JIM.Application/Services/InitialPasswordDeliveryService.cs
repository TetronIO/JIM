// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Services;

/// <summary>
/// Gives a newly provisioned account its first password, and says what came of it.
/// <para>
/// Deliberately free of persistence. This decides what to generate, sends it, and classifies the answer; the
/// caller records the outcome. Keeping it that way means the interesting behaviour, which failures are retried
/// and which are not, is testable without a database, and lets the administrator's own set-password dialog
/// reach the same decisions the export path does rather than growing a second copy of them.
/// </para>
/// <para>
/// <b>The generated password does not leave this class.</b> It is produced, handed to the Connector, and
/// dropped. The result carries the outcome and the target's reason, never the value.
/// </para>
/// </summary>
public class InitialPasswordDeliveryService
{
    private readonly IPasswordGeneratorService _passwordGenerator;

    public InitialPasswordDeliveryService(IPasswordGeneratorService passwordGenerator)
    {
        _passwordGenerator = passwordGenerator;
    }

    /// <summary>
    /// Generates a password for one account and sets it through the Connector.
    /// <para>
    /// Never throws for a failure to set the password. Everything that can go wrong is classified into an
    /// outcome, because the caller is in the middle of an export whose objects were created successfully, and a
    /// password that could not be set must not take that down with it.
    /// </para>
    /// </summary>
    /// <param name="configuration">
    /// What the Synchronisation Rule asks for. Null, or present but switched off, means there is nothing to do.
    /// </param>
    /// <param name="discoveredPolicy">
    /// The password policy JIM read from the Connected System, used when the configuration follows it. Null when
    /// nothing was discovered, in which case JIM's own defaults apply.
    /// </param>
    public async Task<InitialPasswordDeliveryResult> DeliverAsync(
        IConnectorPasswordManagement connector,
        ConnectedSystemObject target,
        SyncRuleInitialPassword? configuration,
        ConnectedSystemPasswordPolicy? discoveredPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(target);

        if (configuration is not { Enabled: true })
            return InitialPasswordDeliveryResult.NotApplicable(
                "This Synchronisation Rule does not set an initial password on the accounts it provisions.");

        var policy = ResolvePolicy(configuration, discoveredPolicy);

        // Checked before anything is generated or sent. An unsatisfiable configuration is an administrator's to
        // fix, and no number of attempts against the target changes that, so it parks for the same reason a
        // policy rejection does.
        var assessment = _passwordGenerator.Assess(policy, discoveredPolicy);
        if (!assessment.IsUsable)
            return InitialPasswordDeliveryResult.Parked(PasswordSetFailureReason.ConfigurationFault,
                $"JIM did not attempt a password, because this Synchronisation Rule's password settings cannot be satisfied: {string.Join(" ", assessment.Problems)}");

        var options = new PasswordSetOptions
        {
            ExpiryBehaviour = configuration.ExpiryBehaviour,
            EnableAccount = configuration.EnableAccount
        };

        var result = await connector.SetPasswordAsync(target, _passwordGenerator.Generate(policy), options, cancellationToken);

        if (result.Success)
            // The applied behaviour, not the requested one: a directory with no equivalent of what was asked for
            // reports what it did instead, and recording the request would misstate the account's actual state.
            return InitialPasswordDeliveryResult.Delivered(
                result.AppliedExpiryBehaviour ?? configuration.ExpiryBehaviour, result.ExpiryBehaviourWarning);

        return Classify(result);
    }

    /// <summary>
    /// Works out which generator settings apply.
    /// <para>
    /// Following the Connected System means re-deriving from what JIM last discovered, so a target whose policy
    /// has been re-read and changed is honoured on the next delivery without an administrator touching
    /// anything. Custom means exactly what the administrator saved, which JIM will not quietly change under
    /// them because a target published something different.
    /// </para>
    /// </summary>
    public PasswordGenerationPolicy ResolvePolicy(SyncRuleInitialPassword configuration, ConnectedSystemPasswordPolicy? discoveredPolicy)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.Source == InitialPasswordSource.Custom
            ? configuration.CustomPolicy
            : _passwordGenerator.DeriveFrom(discoveredPolicy);
    }

    /// <summary>
    /// Turns the Connector's classification into a decision about whether to try again.
    /// <para>
    /// The split is the whole point, and it is not the same as "how bad is this". It is whether anything other
    /// than a person changing the configuration could produce a different answer. A directory that was
    /// unreachable may be reachable on the next run; an account that was not found immediately after being
    /// created is usually replication rather than absence; a service account missing a right will work the
    /// moment the right is granted, with the configuration untouched. A password the target refused, and a
    /// target that cannot set passwords on this kind of object at all, will answer identically for ever.
    /// </para>
    /// </summary>
    private static InitialPasswordDeliveryResult Classify(PasswordSetResult result)
    {
        var message = result.ErrorMessage ?? "The Connected System refused the password without saying why.";

        return result.FailureReason switch
        {
            PasswordSetFailureReason.PolicyRejection => InitialPasswordDeliveryResult.Parked(result.FailureReason, message),
            PasswordSetFailureReason.UnsupportedOperation => InitialPasswordDeliveryResult.Parked(result.FailureReason, message),
            _ => InitialPasswordDeliveryResult.Retry(result.FailureReason, message)
        };
    }
}
