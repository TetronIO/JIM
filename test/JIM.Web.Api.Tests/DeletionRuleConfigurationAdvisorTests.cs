// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests the deletion-rule configuration advisory (#1570): when a Metaverse Object Type uses
/// When Last Connector Disconnected and provisioning export Synchronisation Rules exist for it,
/// provisioned target accounts will keep its objects alive after their last source departs, and the
/// administrator is advised of the consequence (values preserved as last known state) and of the
/// alternative (When Authoritative Source Disconnected). All three surfaces (portal, REST, PowerShell)
/// derive the advisory from this one helper so they can never disagree.
/// </summary>
[TestFixture]
public class DeletionRuleConfigurationAdvisorTests
{
    private const int TypeId = 7;

    private static SyncRule BuildRule(int typeId = TypeId, SyncRuleDirection direction = SyncRuleDirection.Export,
        bool enabled = true, bool? provisions = true, int id = 1) => new()
    {
        Id = id,
        Name = $"Rule {id}",
        ConnectedSystemId = 20,
        MetaverseObjectTypeId = typeId,
        Direction = direction,
        Enabled = enabled,
        ProvisionToConnectedSystem = provisions
    };

    [Test]
    public void GetAdvisory_LastConnectorRuleWithProvisioningExport_ReturnsAdvisory()
    {
        var advisory = DeletionRuleConfigurationAdvisor.GetAdvisory(
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TypeId, [BuildRule()]);

        Assert.That(advisory, Is.Not.Null.And.Contain("When Authoritative Source Disconnected"),
            "the advisory must name the alternative rule the administrator most likely wants");
    }

    [Test]
    public void GetAdvisory_OtherDeletionRules_ReturnsNull()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletionRuleConfigurationAdvisor.GetAdvisory(
                MetaverseObjectDeletionRule.Manual, TypeId, [BuildRule()]), Is.Null);
            Assert.That(DeletionRuleConfigurationAdvisor.GetAdvisory(
                MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, TypeId, [BuildRule()]), Is.Null);
        }
    }

    [Test]
    public void GetAdvisory_NoProvisioningExportForTheType_ReturnsNull()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletionRuleConfigurationAdvisor.GetAdvisory(
                    MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TypeId, []),
                Is.Null, "no rules at all");
            Assert.That(DeletionRuleConfigurationAdvisor.GetAdvisory(
                    MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TypeId, [BuildRule(direction: SyncRuleDirection.Import)]),
                Is.Null, "an import rule provisions nothing");
            Assert.That(DeletionRuleConfigurationAdvisor.GetAdvisory(
                    MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TypeId, [BuildRule(enabled: false)]),
                Is.Null, "a disabled rule provisions nothing");
            Assert.That(DeletionRuleConfigurationAdvisor.GetAdvisory(
                    MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TypeId, [BuildRule(provisions: false)]),
                Is.Null, "an export rule that never provisions keeps no objects alive");
            Assert.That(DeletionRuleConfigurationAdvisor.GetAdvisory(
                    MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TypeId, [BuildRule(provisions: null)]),
                Is.Null, "an unset provisioning flag means the rule does not provision");
            Assert.That(DeletionRuleConfigurationAdvisor.GetAdvisory(
                    MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TypeId, [BuildRule(typeId: TypeId + 1)]),
                Is.Null, "a provisioning rule for a different type keeps this type's objects alive not at all");
        }
    }
}
