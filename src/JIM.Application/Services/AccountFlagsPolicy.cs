// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Services;

/// <summary>
/// Decides what delivering a password may write to the account's flags: whether it is enabled (#2064), and whether its
/// password never expires (#2159). On Active Directory both live in one attribute, userAccountControl, so one ownership
/// ladder decides both, first match wins:
/// <list type="number">
/// <item>An administrator who named the account decides what that set does: to enable or disable it where they chose,
/// and whether the password never expires, which every explicit set chooses through its expiry behaviour. Enforce State
/// puts back a flow's value where it disagrees.</item>
/// <item>The enabled state only: an account whose enable JIM held back only because Active Directory refuses an
/// enabled account with no password, while the Attribute Flow still asks for it enabled, is enabled. The password is
/// what it was waiting for. The hold is placed on the Create that provisions the account (#2039), or on an Update that
/// enables it while a password is still owed (#2158).</item>
/// <item>The account's first password, where an export wrote the account's flags, leaves both to the Attribute Flow
/// behind that export. These are the cases the ladder exists for: an account the flow deliberately creates disabled (a
/// pre-hire, or a person archived at creation) used to be enabled with a password JIM knows; and a flow asserting
/// "Password never expires" (66048) used to have it cleared by an Initial Password that asked for the password to
/// expire.</item>
/// <item>The account's first password, where nothing writes the flags, follows the provisioning Synchronisation Rule's
/// Initial Password: Enable Account on enables it (off leaves it as it was created), and its expiry sets "Password never
/// expires" for Never expires and clears it otherwise.</item>
/// <item>Anything else leaves the flags as the account has them. A propagated password reaches accounts an
/// administrator may have disabled, or exempted from expiry, on purpose, and changing either as a side effect of a
/// password change elsewhere would undo that silently.</item>
/// </list>
/// <para>
/// The account's first password is its Initial Password, or a password change delivered in its place (#2160): one that
/// was already waiting when the account was provisioned, or that arrived before the Initial Password landed. Passwords
/// replace each other; the Initial Password's promises about the account carry forward, so rungs 3 and 4 apply to the
/// change exactly as to the Initial Password, and an administrator's set with no enable choice follows the rule's
/// Enable Account while the account is still owed its first password.
/// </para>
/// <para>
/// "Must change at next sign-in" is not a flag in this sense: it belongs to the password and is applied with it whatever
/// this decides, which is also why a change standing in for the Initial Password keeps its own expiry rather than the
/// rule's. Where "Password never expires" stays set, the directory ignores it, and the delivery says so.
/// </para>
/// <para>
/// The engine decides; the Connector only reports which exports wrote the flags and applies the answer. Kept pure so
/// the ladder is tested as a table, and so every delivery states the rung that decided each flag.
/// </para>
/// </summary>
public static class AccountFlagsPolicy
{
    /// <summary>
    /// Decides what the delivery may write to the account's enabled state and to "Password never expires".
    /// </summary>
    /// <param name="origin">Where the queued password change came from.</param>
    /// <param name="administratorEnableChoice">
    /// The enable decision an administrator made with an <see cref="PendingPasswordChangeOrigin.Explicit"/> set, or
    /// null for none. Ignored for every other origin, which carries no decision of its own.
    /// </param>
    /// <param name="requestedExpiry">
    /// The expiry behaviour the password was asked for with: the administrator's choice for an explicit set, the
    /// provisioning Synchronisation Rule's for an Initial Password, the change's own for a propagated password. Decides
    /// "Password never expires" only for an explicit set; elsewhere <paramref name="firstPassword"/> does.
    /// </param>
    /// <param name="accountFlagsWrittenByExport">
    /// Whether an export wrote the attribute holding the account's flags
    /// (<see cref="PendingPasswordChange.EnabledStateSetByAttributeFlow"/>, a name that predates the second flag). Read
    /// only for the account's first password.
    /// </param>
    /// <param name="firstPassword">
    /// The provisioning Synchronisation Rule's promises about the account, read fresh at the attempt, where this delivery
    /// is the account's first password: its Initial Password, or a change delivered in its place (#2160). Null for any
    /// other delivery, and where the rule no longer sets an Initial Password.
    /// </param>
    public static AccountFlagsDecision Decide(
        PendingPasswordChangeOrigin origin,
        bool? administratorEnableChoice,
        PasswordExpiryBehaviour requestedExpiry,
        bool accountFlagsWrittenByExport,
        InitialPasswordAccountFlags? firstPassword,
        bool accountHoldsWithheldEnable)
    {
        var (enableAccount, enablementBasis) = DecideEnabledState(
            origin, administratorEnableChoice, accountFlagsWrittenByExport, firstPassword, accountHoldsWithheldEnable);
        var (passwordNeverExpires, passwordNeverExpiresBasis) = DecidePasswordNeverExpires(
            origin, requestedExpiry, accountFlagsWrittenByExport, firstPassword);

        return new AccountFlagsDecision(enableAccount, enablementBasis, passwordNeverExpires, passwordNeverExpiresBasis);
    }

    private static (bool? EnableAccount, AccountFlagBasis Basis) DecideEnabledState(
        PendingPasswordChangeOrigin origin,
        bool? administratorEnableChoice,
        bool accountFlagsWrittenByExport,
        InitialPasswordAccountFlags? firstPassword,
        bool accountHoldsWithheldEnable)
    {
        if (origin == PendingPasswordChangeOrigin.Explicit && administratorEnableChoice.HasValue)
            return (administratorEnableChoice, AccountFlagBasis.AdministratorChoice);

        if (accountHoldsWithheldEnable)
            return (true, AccountFlagBasis.CompletesWithheldEnable);

        if (firstPassword == null)
            return (null, AccountFlagBasis.LeftAsIs);

        if (accountFlagsWrittenByExport)
            return (null, AccountFlagBasis.LeftToAttributeFlow);

        // Off leaves the account as it was created rather than disabling it: an account nothing sets the enabled
        // state of is created disabled by the directory anyway, and a late delivery must not disable one a separate
        // process has since enabled.
        return (firstPassword.EnableAccount ? true : null, AccountFlagBasis.SyncRuleSetting);
    }

    /// <summary>
    /// The same ladder for "Password never expires", less the withheld enable, which concerns the enabled state alone.
    /// An explicit set always decides it, because an expiry behaviour is always part of one; the account's first
    /// password decides it from the rule's expiry where nothing else owns it; anything else leaves it.
    /// </summary>
    private static (bool? PasswordNeverExpires, AccountFlagBasis Basis) DecidePasswordNeverExpires(
        PendingPasswordChangeOrigin origin,
        PasswordExpiryBehaviour requestedExpiry,
        bool accountFlagsWrittenByExport,
        InitialPasswordAccountFlags? firstPassword)
    {
        if (origin == PendingPasswordChangeOrigin.Explicit)
            return (requestedExpiry == PasswordExpiryBehaviour.NeverExpires, AccountFlagBasis.AdministratorChoice);

        if (firstPassword == null)
            return (null, AccountFlagBasis.LeftAsIs);

        return accountFlagsWrittenByExport
            ? (null, AccountFlagBasis.LeftToAttributeFlow)
            : (firstPassword.ExpiryBehaviour == PasswordExpiryBehaviour.NeverExpires, AccountFlagBasis.SyncRuleSetting);
    }

    /// <summary>
    /// The expiry behaviour to send the Connector with the password, which must agree with "Password never expires"
    /// wherever the decision writes that flag, because the Connector sets the flag for Never expires and clears it for
    /// the other two. The password's own expiry otherwise: "must change at next sign-in" belongs to the password being
    /// delivered, so a change delivered in place of an Initial Password keeps its own (#2160), and takes Never expires
    /// only where the account's promise sets the flag. Where the decision clears the flag, a password asked to never
    /// expire (a propagated change queued before that was refused, #2159) expires by the target's policy instead.
    /// </summary>
    public static PasswordExpiryBehaviour ExpiryToSend(PasswordExpiryBehaviour passwordExpiry, AccountFlagsDecision decision) =>
        decision.PasswordNeverExpires switch
        {
            true => PasswordExpiryBehaviour.NeverExpires,
            false when passwordExpiry == PasswordExpiryBehaviour.NeverExpires => PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
            _ => passwordExpiry
        };

    /// <summary>
    /// Whether the delivery has to wait while an export to the account is in flight. An export records what it did to
    /// the account's flags only once its batch has come back from the Connector, and one still being sent may be doing
    /// any of three things this delivery must see first:
    /// <list type="bullet">
    /// <item>disabling the account (a leaver, say) that this decision would enable (#2173);</item>
    /// <item>writing the flags as the Attribute Flow that owns them, which this decision would otherwise set from the
    /// Synchronisation Rule (#2159);</item>
    /// <item>enabling an account that has no password, which Active Directory refuses, so the Connector holds the enable
    /// back for this very delivery to complete (#2158).</item>
    /// </list>
    /// So every decision JIM takes waits for it, whether it writes a flag or not. An administrator's choice of the
    /// enabled state does not: it is applied whatever the export writes.
    /// </summary>
    public static bool WaitsForAnExportInFlight(AccountFlagsDecision decision) =>
        decision.EnablementBasis != AccountFlagBasis.AdministratorChoice;

    /// <summary>
    /// Whether <see cref="Decide"/>'s answer can depend on a withheld enable, so the delivery needs to probe for one.
    /// False only where an administrator has already decided the enabled state, which nothing else overrides.
    /// </summary>
    public static bool DependsOnWithheldEnable(PendingPasswordChangeOrigin origin, bool? administratorChoice) =>
        !(origin == PendingPasswordChangeOrigin.Explicit && administratorChoice.HasValue);
}
