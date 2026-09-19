// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using System.Security.Cryptography;

namespace JIM.Application.Services;

/// <summary>
/// Decides which password an initial-password configuration resolves to, or why none can be sent (#1697).
/// <para>
/// Split out on its own so the decision is reusable wherever a first
/// password needs resolving without needing a Connector to send it to: the staging path queues a
/// <see cref="PendingPasswordChangeOrigin.Provisioned"/> row without ever generating a value, and resolves the
/// password only when the Password Delivery Service is about to attempt it.
/// </para>
/// <para>
/// <b>No password leaks out through a refusal.</b> A refusal's message and failure reason are the only things
/// returned when nothing can be sent; the password behind an unsatisfiable static configuration, and the
/// ciphertext it was decrypted from, both stay inside this class. Only a genuinely usable resolution carries a
/// value, and it exists to be sent, not logged.
/// </para>
/// </summary>
public class InitialPasswordResolver
{
    private readonly IPasswordGeneratorService _passwordGenerator;
    private readonly ICredentialProtection _credentialProtection;

    /// <param name="credentialProtection">
    /// Decrypts the static password an administrator chose, which is the only password JIM stores. Held here
    /// rather than passed per resolution because it is a property of the deployment, not of the account.
    /// </param>
    public InitialPasswordResolver(IPasswordGeneratorService passwordGenerator, ICredentialProtection credentialProtection)
    {
        _passwordGenerator = passwordGenerator;
        _credentialProtection = credentialProtection;
    }

    /// <summary>
    /// Resolves the password a Synchronisation Rule's initial-password settings would produce for one account,
    /// or explains why none can be. Static source resolves the static password; otherwise generates.
    /// </summary>
    /// <param name="configuration">What the Synchronisation Rule asks for.</param>
    /// <param name="discoveredPolicy">
    /// The password policy JIM read from the Connected System, used when the configuration follows it. Null when
    /// nothing was discovered, in which case JIM's own defaults apply.
    /// </param>
    public InitialPasswordResolution Resolve(SyncRuleInitialPassword configuration, ConnectedSystemPasswordPolicy? discoveredPolicy)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.Source == InitialPasswordSource.Static
            ? ResolveStaticPassword(configuration, discoveredPolicy)
            : GeneratePassword(configuration, discoveredPolicy);
    }

    /// <summary>
    /// Generates a password from the rule's settings, or explains why none can be.
    /// </summary>
    private InitialPasswordResolution GeneratePassword(
        SyncRuleInitialPassword configuration, ConnectedSystemPasswordPolicy? discoveredPolicy)
    {
        var policy = ResolvePolicy(configuration, discoveredPolicy);

        var assessment = _passwordGenerator.Assess(policy, discoveredPolicy);
        if (!assessment.IsUsable)
            return InitialPasswordResolution.Refused(PasswordSetFailureReason.ConfigurationFault,
                $"JIM did not attempt a password, because this Synchronisation Rule's password settings cannot be satisfied: {string.Join(" ", assessment.Problems)}");

        return InitialPasswordResolution.Usable(_passwordGenerator.Generate(policy));
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
    private InitialPasswordResolution ResolveStaticPassword(
        SyncRuleInitialPassword configuration, ConnectedSystemPasswordPolicy? discoveredPolicy)
    {
        if (string.IsNullOrEmpty(configuration.StaticPasswordEncryptedValue))
            return InitialPasswordResolution.Refused(PasswordSetFailureReason.ConfigurationFault,
                "JIM did not attempt a password, because this Synchronisation Rule is set to use one password for every " +
                "account it provisions and no password has been set. Generating one instead would leave nobody able to " +
                "tell the account holder what it is.");

        string? password;
        try
        {
            password = _credentialProtection.Unprotect(configuration.StaticPasswordEncryptedValue);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // The exception's own message is used rather than the value it failed on, and the stored ciphertext
            // is deliberately not included: this string is displayed, logged and carried on Activities.
            return InitialPasswordResolution.Refused(PasswordSetFailureReason.ConfigurationFault,
                "JIM could not decrypt the password stored on this Synchronisation Rule, which usually means the " +
                $"deployment's encryption key has been changed or lost. Set the password again to repair it. ({ex.Message})");
        }

        if (string.IsNullOrWhiteSpace(password))
            return InitialPasswordResolution.Refused(PasswordSetFailureReason.ConfigurationFault,
                "JIM did not attempt a password, because the password stored on this Synchronisation Rule is empty.");

        // Assessed for the same reason a generator configuration is, and with more at stake: one password is
        // going to every account this rule provisions, so a rejection is not one account's problem.
        var assessment = _passwordGenerator.AssessSupplied(password, discoveredPolicy);
        if (!assessment.IsUsable)
            return InitialPasswordResolution.Refused(PasswordSetFailureReason.ConfigurationFault,
                "JIM did not attempt a password, because the password set on this Synchronisation Rule will not be " +
                $"accepted by this Connected System: {string.Join(" ", assessment.Problems)}");

        return InitialPasswordResolution.Usable(password);
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
}
