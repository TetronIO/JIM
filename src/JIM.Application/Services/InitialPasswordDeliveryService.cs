// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using System.Security.Cryptography;

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
/// <b>No password leaves this class.</b> A generated one is produced, handed to the Connector and dropped; a
/// static one is decrypted, handed to the Connector and dropped. The result carries the outcome and the target's
/// reason, never a value, and neither does anything this class logs.
/// </para>
/// </summary>
public class InitialPasswordDeliveryService
{
    private readonly IPasswordGeneratorService _passwordGenerator;
    private readonly ICredentialProtection _credentialProtection;

    /// <param name="credentialProtection">
    /// Decrypts the static password an administrator chose, which is the only password JIM stores. Held here
    /// rather than passed per delivery because it is a property of the deployment, not of the account.
    /// </param>
    public InitialPasswordDeliveryService(IPasswordGeneratorService passwordGenerator, ICredentialProtection credentialProtection)
    {
        _passwordGenerator = passwordGenerator;
        _credentialProtection = credentialProtection;
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

        // Which password to set is decided before anything is sent, and either answer can be a reason not to
        // send at all. An unsatisfiable configuration is an administrator's to fix, and no number of attempts
        // against the target changes that, so it parks for the same reason a policy rejection does.
        var (password, refusal) = configuration.Source == InitialPasswordSource.Static
            ? ResolveStaticPassword(configuration, discoveredPolicy)
            : GeneratePassword(configuration, discoveredPolicy);

        if (refusal != null)
            return refusal;

        var options = new PasswordSetOptions
        {
            ExpiryBehaviour = configuration.ExpiryBehaviour,
            EnableAccount = configuration.EnableAccount
        };

        var result = await connector.SetPasswordAsync(target, password!, options, cancellationToken);

        if (result.Success)
            // The applied behaviour, not the requested one: a directory with no equivalent of what was asked for
            // reports what it did instead, and recording the request would misstate the account's actual state.
            return InitialPasswordDeliveryResult.Delivered(
                result.AppliedExpiryBehaviour ?? configuration.ExpiryBehaviour, result.ExpiryBehaviourWarning);

        return Classify(result);
    }

    /// <summary>
    /// Generates a password from the rule's settings, or explains why none can be.
    /// </summary>
    private (string? Password, InitialPasswordDeliveryResult? Refusal) GeneratePassword(
        SyncRuleInitialPassword configuration, ConnectedSystemPasswordPolicy? discoveredPolicy)
    {
        var policy = ResolvePolicy(configuration, discoveredPolicy);

        var assessment = _passwordGenerator.Assess(policy, discoveredPolicy);
        if (!assessment.IsUsable)
            return (null, InitialPasswordDeliveryResult.Parked(PasswordSetFailureReason.ConfigurationFault,
                $"JIM did not attempt a password, because this Synchronisation Rule's password settings cannot be satisfied: {string.Join(" ", assessment.Problems)}"));

        return (_passwordGenerator.Generate(policy), null);
    }

    /// <summary>
    /// Reads back the one password an administrator chose for every account this rule provisions, or explains why
    /// it cannot be used.
    /// <para>
    /// Everything that can go wrong here parks rather than retries, and all of it for the same reason: an absent
    /// password, an encryption key that no longer opens it, and a value the target will refuse are each resolved
    /// only by a person changing something. Retrying in the meantime reaches an identical answer while inflating
    /// an attempt count that is supposed to mean "distinct configurations tried".
    /// </para>
    /// <para>
    /// The password and the stored ciphertext both stay inside this method. What comes back is either a value to
    /// send or a reason to show, and a reason is written to Activities, logs and the portal.
    /// </para>
    /// </summary>
    private (string? Password, InitialPasswordDeliveryResult? Refusal) ResolveStaticPassword(
        SyncRuleInitialPassword configuration, ConnectedSystemPasswordPolicy? discoveredPolicy)
    {
        if (string.IsNullOrEmpty(configuration.StaticPasswordEncryptedValue))
            return (null, InitialPasswordDeliveryResult.Parked(PasswordSetFailureReason.ConfigurationFault,
                "JIM did not attempt a password, because this Synchronisation Rule is set to use one password for every " +
                "account it provisions and no password has been set. Generating one instead would leave nobody able to " +
                "tell the account holder what it is."));

        string? password;
        try
        {
            password = _credentialProtection.Unprotect(configuration.StaticPasswordEncryptedValue);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // The exception's own message is used rather than the value it failed on, and the stored ciphertext
            // is deliberately not included: this string is displayed, logged and carried on Activities.
            return (null, InitialPasswordDeliveryResult.Parked(PasswordSetFailureReason.ConfigurationFault,
                "JIM could not decrypt the password stored on this Synchronisation Rule, which usually means the " +
                $"deployment's encryption key has been changed or lost. Set the password again to repair it. ({ex.Message})"));
        }

        if (string.IsNullOrWhiteSpace(password))
            return (null, InitialPasswordDeliveryResult.Parked(PasswordSetFailureReason.ConfigurationFault,
                "JIM did not attempt a password, because the password stored on this Synchronisation Rule is empty."));

        // Assessed for the same reason a generator configuration is, and with more at stake: one password is
        // going to every account this rule provisions, so a rejection is not one account's problem.
        var assessment = _passwordGenerator.AssessSupplied(password, discoveredPolicy);
        if (!assessment.IsUsable)
            return (null, InitialPasswordDeliveryResult.Parked(PasswordSetFailureReason.ConfigurationFault,
                "JIM did not attempt a password, because the password set on this Synchronisation Rule will not be " +
                $"accepted by this Connected System: {string.Join(" ", assessment.Problems)}"));

        return (password, null);
    }

    /// <summary>
    /// Works out which generator settings apply.
    /// <para>
    /// Following the Connected System means re-deriving from what JIM last discovered, so a target whose policy
    /// has been re-read and changed is honoured on the next delivery without an administrator touching
    /// anything. Custom means exactly what the administrator saved, which JIM will not quietly change under
    /// them because a target published something different.
    /// </para>
    /// <para>
    /// Meaningful only for the sources that generate. A rule set to
    /// <see cref="InitialPasswordSource.Static"/> generates nothing, and the settings this returns for it are
    /// the ones the rule would fall back to were the source changed, not the ones in use.
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
