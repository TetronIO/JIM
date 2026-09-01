// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// Advises an administrator configuring a Metaverse Object Type's deletion rule when the configuration
/// will keep objects alive after their last source departs (#1570). Under When Last Connector
/// Disconnected, a provisioned target account counts as a connector, so an object of the type outlives
/// its last source while a target account exists and the departed source's values are preserved on it as
/// last known state; that is usually not what a source-of-record departure is meant to do, and the
/// advisory names the alternative. All three surfaces (the portal's deletion rule editor, the REST object
/// type responses, and the PowerShell cmdlets that read them) derive the advisory from this one helper so
/// they can never disagree.
/// </summary>
public static class DeletionRuleConfigurationAdvisor
{
    /// <summary>
    /// The advisory for the given (proposed or stored) deletion rule against the current Synchronisation
    /// Rules, or null when the configuration needs no advice: any rule other than When Last Connector
    /// Disconnected, or no enabled provisioning export Synchronisation Rule exists for the type (with no
    /// provisioned targets, the last connector genuinely is the last source).
    /// </summary>
    /// <param name="deletionRule">The deletion rule being configured or displayed.</param>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type the rule belongs to.</param>
    /// <param name="allSyncRules">Every Synchronisation Rule, from which the type's provisioning exports are found.</param>
    public static string? GetAdvisory(MetaverseObjectDeletionRule deletionRule, int metaverseObjectTypeId, IEnumerable<SyncRule> allSyncRules)
    {
        var typeHasProvisioningExport = allSyncRules.Any(rule =>
            rule.Enabled
            && rule.Direction == SyncRuleDirection.Export
            && rule.ProvisionToConnectedSystem == true
            && rule.MetaverseObjectTypeId == metaverseObjectTypeId);
        return GetAdvisory(deletionRule, typeHasProvisioningExport);
    }

    /// <summary>
    /// The advisory where the caller has already answered whether the type has an enabled provisioning
    /// export Synchronisation Rule (the portal's deletion rule editor holds the type's export rule headers,
    /// so it answers from those rather than re-querying every rule).
    /// </summary>
    public static string? GetAdvisory(MetaverseObjectDeletionRule deletionRule, bool typeHasEnabledProvisioningExport)
    {
        if (deletionRule != MetaverseObjectDeletionRule.WhenLastConnectorDisconnected || !typeHasEnabledProvisioningExport)
            return null;

        return "Provisioned target accounts count as connectors under When Last Connector Disconnected. " +
            "An object of this type will outlive its last source while a target account exists, and the departed " +
            "source's attribute values will be preserved on it as last known state. If the departure of your " +
            "source of record should deprovision target accounts instead, use When Authoritative Source " +
            "Disconnected and list the source system(s).";
    }
}
