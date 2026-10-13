// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// Which Attribute Flows write an account's flags (#2159). On Active Directory and Samba AD the attribute
/// <c>userAccountControl</c> carries both whether the account is enabled and whether its password never expires, and
/// whatever writes it owns both: a delivered password leaves them to that Attribute Flow. One place answers the question
/// for the engine and the portal, so the two cannot disagree on what "maps userAccountControl" means.
/// </summary>
public static class AccountControlAttributeFlows
{
    /// <summary>
    /// The Active Directory attribute holding an account's flags, matched without regard to case as directories do.
    /// </summary>
    public const string AttributeName = "userAccountControl";

    /// <summary>
    /// Whether the attribute is the one holding an account's flags.
    /// </summary>
    public static bool IsAccountControlAttribute(string? attributeName) =>
        string.Equals(attributeName, AttributeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the Attribute Flow writes the account's flags: it is switched on and targets <c>userAccountControl</c>.
    /// </summary>
    public static bool WritesAccountControl(SyncRuleMapping mapping) =>
        mapping.Enabled && IsAccountControlAttribute(mapping.TargetConnectedSystemAttribute?.Name);

    /// <summary>
    /// Whether the Synchronisation Rule exports an Attribute Flow that writes the account's flags.
    /// </summary>
    public static bool MapsAccountControl(SyncRule syncRule) =>
        syncRule.Direction == SyncRuleDirection.Export && syncRule.AttributeFlowRules.Any(WritesAccountControl);

    /// <summary>
    /// Whether the Synchronisation Rule puts its own userAccountControl value back on accounts of the Object Type
    /// whenever it drifts: an enabled export rule for the type, with Enforce State on, whose Attribute Flow writes the
    /// attribute on updates as well as on creation. Anything else that changes the flags on such an account (an
    /// administrator's password reset choosing an expiry, say) lasts only until the next synchronisation.
    /// </summary>
    public static bool EnforcesAccountControl(SyncRule syncRule, int connectedSystemObjectTypeId) =>
        syncRule.Enabled &&
        syncRule.EnforceState &&
        syncRule.Direction == SyncRuleDirection.Export &&
        syncRule.ConnectedSystemObjectTypeId == connectedSystemObjectTypeId &&
        syncRule.AttributeFlowRules.Any(mapping => WritesAccountControl(mapping) && mapping.FlowsOnUpdateExport());
}
