// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// How several export Synchronisation Rules covering one account are staged together (#2826): one group per Connected
/// System and Object Type, the rule that manages the account (or would provision it) first, and each target attribute owned
/// by the one rule that writes it. An attribute two rules write is a clash: neither stages it.
/// </summary>
[TestFixture]
public class SyncEngineExportGroupPlanTests
{
    private const int AdId = 1;
    private const int HrId = 2;
    private const int UserTypeId = 5;
    private const int ContactTypeId = 6;

    private static readonly ConnectedSystemObjectTypeAttribute GivenName = new() { Id = 101, Name = "givenName" };
    private static readonly ConnectedSystemObjectTypeAttribute Mail = new() { Id = 102, Name = "mail" };
    private static readonly ConnectedSystemObjectTypeAttribute Title = new() { Id = 103, Name = "title" };
    private static readonly ConnectedSystemObjectTypeAttribute ExtensionAttribute10 = new() { Id = 104, Name = "extensionAttribute10" };
    private static readonly ConnectedSystemObjectTypeAttribute Member = new() { Id = 105, Name = "member", AttributePlurality = AttributePlurality.MultiValued };

    [Test]
    public void PlanExportGroups_RulesForDifferentSystemsAndTypes_AreSeparateGroups()
    {
        var adUsers = Rule(10, provisions: true, writes: [GivenName]);
        var adContacts = Rule(11, provisions: true, csoTypeId: ContactTypeId, writes: [Mail]);
        var hr = Rule(12, provisions: true, systemId: HrId, writes: [Title]);

        var groups = SyncEngine.PlanExportGroups([adUsers, adContacts, hr], _ => null);

        Assert.That(groups.Select(g => g.OrderedRules.Single()), Is.EquivalentTo(new[] { adUsers, adContacts, hr }));
    }

    [Test]
    public void PlanExportGroups_TwoRulesWritingDifferentAttributes_AreOneGroupWithNoClash()
    {
        var baseAccount = Rule(10, provisions: true, writes: [GivenName, Mail]);
        var managerAttributes = Rule(11, provisions: false, writes: [ExtensionAttribute10]);

        var group = SyncEngine.PlanExportGroups([managerAttributes, baseAccount], _ => null).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.OrderedRules, Is.EqualTo(new[] { baseAccount, managerAttributes }),
                "the rule that provisions goes first, so the add-on rule's changes join its Create");
            Assert.That(group.ClashedAttributeIds, Is.Empty);
            Assert.That(group.ClashingRules, Is.Empty);
        }
    }

    [Test]
    public void PlanExportGroups_AnAccountTheAddOnRuleManages_PutsTheManagingRuleFirst()
    {
        // The managing rule leads even where another rule provisions, so the join stays with it.
        var provisioning = Rule(10, provisions: true, writes: [GivenName]);
        var managing = Rule(11, provisions: true, writes: [Mail]);
        var account = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = AdId, TypeId = UserTypeId, MetaverseObjectId = Guid.NewGuid() };
        account.RecordJoin(ConnectedSystemObjectJoinMethod.Provisioning, managing, DateTime.UtcNow);

        var group = SyncEngine.PlanExportGroups([provisioning, managing], _ => account).Single();

        Assert.That(group.OrderedRules, Is.EqualTo(new[] { managing, provisioning }));
    }

    [Test]
    public void PlanExportGroups_NoRuleProvisions_OrdersByRuleId()
    {
        var second = Rule(20, provisions: false, writes: [GivenName]);
        var first = Rule(15, provisions: false, writes: [Mail]);

        var group = SyncEngine.PlanExportGroups([second, first], _ => null).Single();

        Assert.That(group.OrderedRules, Is.EqualTo(new[] { first, second }));
    }

    [Test]
    public void PlanExportGroups_TwoRulesWritingTitle_ClashOnTitleOnly()
    {
        var baseAccount = Rule(10, provisions: true, writes: [GivenName, Title]);
        var managerAttributes = Rule(11, provisions: false, writes: [Title, ExtensionAttribute10]);

        var group = SyncEngine.PlanExportGroups([baseAccount, managerAttributes], _ => null).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.ClashedAttributeIds, Is.EquivalentTo(new[] { Title.Id }));
            Assert.That(group.ClashedAttributeNames, Is.EqualTo(new[] { "title" }));
            Assert.That(group.ClashingRules, Is.EqualTo(new[] { baseAccount, managerAttributes }));
        }
    }

    [Test]
    public void PlanExportGroups_TwoRulesAddingToOneMultiValuedAttribute_Clash()
    {
        // Combining values from several rules needs per-value ownership, which is #2827's.
        var staffGroups = Rule(10, provisions: true, writes: [Member]);
        var projectGroups = Rule(11, provisions: false, writes: [Member]);

        var group = SyncEngine.PlanExportGroups([staffGroups, projectGroups], _ => null).Single();

        Assert.That(group.ClashedAttributeIds, Is.EquivalentTo(new[] { Member.Id }));
    }

    [Test]
    public void PlanExportGroups_ADisabledMapping_WritesNothing()
    {
        var baseAccount = Rule(10, provisions: true, writes: [Title]);
        var managerAttributes = Rule(11, provisions: false, writes: [Title]);
        managerAttributes.AttributeFlowRules.Single().Enabled = false;

        var group = SyncEngine.PlanExportGroups([baseAccount, managerAttributes], _ => null).Single();

        Assert.That(group.ClashedAttributeIds, Is.Empty);
    }

    [Test]
    public void PlanExportGroups_ALoneRule_HasNoClash()
    {
        var group = SyncEngine.PlanExportGroups([Rule(10, provisions: true, writes: [GivenName])], _ => null).Single();

        Assert.That(group.ClashedAttributeIds, Is.Empty);
    }

    private static SyncRule Rule(int id, bool provisions, ConnectedSystemObjectTypeAttribute[] writes, int systemId = AdId, int csoTypeId = UserTypeId)
    {
        var rule = new SyncRule
        {
            Id = id,
            Name = $"Rule {id}",
            ConnectedSystemId = systemId,
            ConnectedSystemObjectTypeId = csoTypeId,
            MetaverseObjectTypeId = 100,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ProvisionToConnectedSystem = provisions
        };
        foreach (var target in writes)
        {
            rule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = rule,
                SyncRuleId = id,
                TargetConnectedSystemAttribute = target,
                TargetConnectedSystemAttributeId = target.Id
            });
        }
        return rule;
    }
}
