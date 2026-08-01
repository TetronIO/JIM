// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Logic;

/// <summary>
/// Whether, and how, a Synchronisation Rule gives a newly provisioned account its first password.
/// <para>
/// Held on the Synchronisation Rule rather than on the Connected System because rules are how JIM
/// distinguishes populations. A rule provisioning contractors and a rule provisioning permanent staff into the
/// same directory can reasonably want different password rules, and there is nowhere else to express that.
/// </para>
/// <para>
/// A row exists only for a rule somebody has configured. Its absence means no initial password, which is the
/// state every rule starts in and stays in until an administrator decides otherwise: JIM setting passwords on
/// accounts nobody asked it to is not a sensible default.
/// </para>
/// </summary>
public class SyncRuleInitialPassword
{
    public int Id { get; set; }

    public SyncRule SyncRule { get; set; } = null!;

    public int SyncRuleId { get; set; }

    /// <summary>
    /// Whether to set a password when this rule provisions an account.
    /// <para>
    /// Separate from the row's existence so an administrator can switch delivery off without losing the
    /// generator configuration they tuned, and switch it back on without rebuilding it.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the generator follows what JIM discovered on the Connected System, or a configuration the
    /// administrator wrote here.
    /// </summary>
    public InitialPasswordSource Source { get; set; } = InitialPasswordSource.Discovered;

    /// <summary>
    /// The generator configuration used when <see cref="Source"/> is
    /// <see cref="InitialPasswordSource.Custom"/>.
    /// <para>
    /// Kept even while the source is Discovered, so that switching between the two is not destructive. It is
    /// seeded from the discovered policy when the section is first configured, which means "Custom" starts from
    /// something that works rather than from nothing.
    /// </para>
    /// </summary>
    public PasswordGenerationPolicy CustomPolicy { get; set; } = new();

    /// <summary>
    /// What should happen to the password once set: whether the account holder must choose a new one at first
    /// sign-in, whether it ages normally, or whether it never expires.
    /// <para>
    /// Requiring a change at next sign-in is the default and is what makes the rest of this feature's handling
    /// of the password proportionate: the generated value is a means of getting the account holder to their own
    /// password, not a credential meant to live for long.
    /// </para>
    /// </summary>
    public PasswordExpiryBehaviour ExpiryBehaviour { get; set; } = PasswordExpiryBehaviour.RequireChangeAtNextSignIn;

    /// <summary>
    /// Whether to enable the account as part of setting its password.
    /// <para>
    /// On by default, because a provisioned account that nobody can sign in to is rarely what was wanted.
    /// Active Directory refuses to enable an account that has no policy-compliant password, so the enable has to
    /// follow the password rather than accompany the create; the Connector owns that ordering.
    /// </para>
    /// </summary>
    public bool EnableAccount { get; set; } = true;
}
