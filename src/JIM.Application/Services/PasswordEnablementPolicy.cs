// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Application.Services;

/// <summary>
/// Decides whether delivering a password enables, disables or leaves the account alone (#2064).
/// <para>
/// The precedence, first match wins:
/// </para>
/// <list type="number">
/// <item>An administrator who named the account and chose to enable or disable it gets that choice.</item>
/// <item>An account whose enable JIM held back only because Active Directory refuses an enabled account with no
/// password, while the Attribute Flow still asks for it enabled, is enabled: the password is what it was waiting for.
/// The hold is placed on the Create that provisions the account (#2039), or on an Update that enables it while a
/// password is still owed (#2158).</item>
/// <item>A first password for an account whose enabled state an export wrote leaves that state to the Attribute Flow.
/// This is the case the policy exists for: an account the flow deliberately creates disabled (a pre-hire, or a person
/// archived at creation) used to be enabled with a password JIM knows.</item>
/// <item>A first password for an account nothing sets the enabled state of follows the provisioning Synchronisation
/// Rule's Enable Account: on enables it, off leaves it as it was created.</item>
/// <item>Anything else leaves the account as it is. A propagated password reaches accounts an administrator may have
/// disabled on purpose, and re-enabling one as a side effect of a password change elsewhere would undo that silently.</item>
/// </list>
/// <para>
/// The engine decides; the Connector only reports which exports wrote the enabled state and applies the answer.
/// Kept pure so the table is tested as a table, and so every delivery states the row that decided it.
/// </para>
/// </summary>
public static class PasswordEnablementPolicy
{
    /// <summary>
    /// Decides what the delivery does to the account's enabled state.
    /// </summary>
    /// <param name="origin">Where the queued password change came from.</param>
    /// <param name="administratorChoice">
    /// The enable decision an administrator made with an <see cref="PendingPasswordChangeOrigin.Explicit"/> set, or
    /// null for none. Ignored for every other origin, which carries no decision of its own.
    /// </param>
    /// <param name="enabledStateSetByAttributeFlow">
    /// Whether an export wrote the attribute through which the account is enabled or disabled
    /// (<see cref="PendingPasswordChange.EnabledStateSetByAttributeFlow"/>). Read only for a
    /// <see cref="PendingPasswordChangeOrigin.Provisioned"/> row.
    /// </param>
    /// <param name="ruleEnableAccount">
    /// The provisioning Synchronisation Rule's Enable Account, read fresh at the attempt. Read only for a
    /// <see cref="PendingPasswordChangeOrigin.Provisioned"/> row.
    /// </param>
    /// <param name="accountHoldsWithheldEnable">
    /// Whether the account still holds an enabled value JIM withheld until a password exists (#2039), probed live at
    /// the attempt. Not needed where <see cref="DependsOnWithheldEnable"/> says so.
    /// </param>
    public static AccountEnablementDecision Decide(
        PendingPasswordChangeOrigin origin,
        bool? administratorChoice,
        bool enabledStateSetByAttributeFlow,
        bool ruleEnableAccount,
        bool accountHoldsWithheldEnable)
    {
        if (origin == PendingPasswordChangeOrigin.Explicit && administratorChoice.HasValue)
            return new AccountEnablementDecision(administratorChoice, AccountEnablementBasis.AdministratorChoice);

        if (accountHoldsWithheldEnable)
            return new AccountEnablementDecision(true, AccountEnablementBasis.CompletesWithheldEnable);

        if (origin != PendingPasswordChangeOrigin.Provisioned)
            return new AccountEnablementDecision(null, AccountEnablementBasis.LeftAsIs);

        if (enabledStateSetByAttributeFlow)
            return new AccountEnablementDecision(null, AccountEnablementBasis.LeftToAttributeFlow);

        // Off leaves the account as it was created rather than disabling it: an account nothing sets the enabled
        // state of is created disabled by the directory anyway, and a late delivery must not disable one a separate
        // process has since enabled.
        return new AccountEnablementDecision(ruleEnableAccount ? true : null, AccountEnablementBasis.SyncRuleSetting);
    }

    /// <summary>
    /// Whether the delivery has to wait while an export to the account is in flight. An export records what it did to
    /// the account's enabled state only once its batch has come back from the Connector, and one still being sent may be
    /// doing either of two things this delivery must see first:
    /// <list type="bullet">
    /// <item>disabling the account (a leaver, say) that this decision would enable (#2173);</item>
    /// <item>enabling an account that has no password, which Active Directory refuses, so the Connector holds the enable
    /// back for this very delivery to complete (#2158). Decided before that hold is recorded, the delivery sets the
    /// password and leaves the account disabled until a later export enables it.</item>
    /// </list>
    /// So every decision JIM takes waits for it, whether it enables the account or not. An administrator's choice does
    /// not: it is applied whatever the export writes.
    /// </summary>
    public static bool WaitsForAnExportInFlight(AccountEnablementDecision decision) =>
        decision.Basis != AccountEnablementBasis.AdministratorChoice;

    /// <summary>
    /// Whether <see cref="Decide"/>'s answer can depend on a withheld enable, so the delivery needs to probe for one.
    /// False only where an administrator has already decided, which nothing else overrides.
    /// </summary>
    public static bool DependsOnWithheldEnable(PendingPasswordChangeOrigin origin, bool? administratorChoice) =>
        !(origin == PendingPasswordChangeOrigin.Explicit && administratorChoice.HasValue);
}
