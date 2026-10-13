// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// When the portal says that a userAccountControl Attribute Flow, rather than a delivered password, decides an
/// account's flags (#2159). On Active Directory that attribute carries whether the account is enabled and whether its
/// password never expires, and a delivered password leaves both to the flow that writes it. Kept apart from the markup
/// so the conditions are tested without rendering anything; which flows count is
/// <see cref="AccountControlAttributeFlows"/>'s, shared with the delivery itself.
/// </summary>
public static class AccountControlFlowNotes
{
    /// <summary>
    /// Whether an Attribute Flow's row says that password delivery leaves the account's flags to it: an enabled export
    /// flow targeting userAccountControl, on a Synchronisation Rule whose accounts are given passwords, through its
    /// Initial Password or through Password Synchronisation configured on its Connected System. Configured rather than
    /// switched on, because a system switched off still holds the passwords and delivers them once switched back on.
    /// </summary>
    public static bool ShowsPasswordDeliveryNote(SyncRule syncRule, SyncRuleMapping mapping, bool passwordSynchronisationConfigured) =>
        syncRule.Direction == SyncRuleDirection.Export &&
        AccountControlAttributeFlows.WritesAccountControl(mapping) &&
        (syncRule.InitialPassword is { Enabled: true } || passwordSynchronisationConfigured);

    /// <summary>
    /// Whether the Initial Password warns that its "Require a change at the next sign-in" may be ignored: the rule writes
    /// userAccountControl, and if that flow sets "Password never expires", Active Directory does not ask for the change.
    /// The flow is an expression, so whether it sets the flag cannot be known here; the warning says "if".
    /// </summary>
    public static bool WarnsRequireChangeMayBeIgnored(SyncRule syncRule, SyncRuleInitialPassword configuration) =>
        WarnsRequireChangeMayBeIgnored(AccountControlAttributeFlows.MapsAccountControl(syncRule), configuration);

    /// <summary>
    /// <see cref="WarnsRequireChangeMayBeIgnored(SyncRule, SyncRuleInitialPassword)"/>, for a component that is handed
    /// whether the rule maps userAccountControl rather than the rule itself, and re-renders as the expiry is edited.
    /// </summary>
    public static bool WarnsRequireChangeMayBeIgnored(bool ruleMapsUserAccountControl, SyncRuleInitialPassword configuration) =>
        ruleMapsUserAccountControl &&
        configuration.Enabled &&
        configuration.ExpiryBehaviour == PasswordExpiryBehaviour.RequireChangeAtNextSignIn;
}
