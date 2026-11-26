// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// What an export Synchronisation Rule leaving a Metaverse Object's scope does to the account it finds (#2826). The rule
/// that manages an account (the one that provisioned it or matched it on export) decides whether it exists: another rule
/// can keep it only by taking it over, which only a rule that provisions may do, and a rule that merely adds attributes to
/// an account another rule manages never deprovisions it. An account no export rule manages (joined by an import rule)
/// keeps the #2822 behaviour: any covering rule keeps it.
/// </summary>
[TestFixture]
public class SyncEngineExportScopeExitTests
{
    private const int SystemId = 1;
    private const int UserTypeId = 5;
    private const int ContactTypeId = 6;

    [Test]
    public void DecideExportScopeExit_ManagingRuleLeavesWhileAProvisioningRuleCovers_KeepsTheAccountForThatRule()
    {
        var contractor = Rule(10, "Contractor accounts", provisions: true);
        var staff = Rule(11, "Staff accounts", provisions: true);

        var decision = SyncEngine.DecideExportScopeExit(contractor, AccountManagedBy(contractor), [staff]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(ExportScopeExitOutcome.KeptByCoveringRule));
            Assert.That(decision.CoveringRule, Is.SameAs(staff));
        }
    }

    [Test]
    public void DecideExportScopeExit_ManagingRuleLeavesWhileOnlyARuleThatDoesNotProvisionCovers_Deprovisions()
    {
        // A leaver whose manager flag HR never cleared: the add-on rule still covers them, but cannot keep the account.
        var baseAccount = Rule(10, "Base account", provisions: true);
        var managerAttributes = Rule(11, "Manager attributes", provisions: false);

        var decision = SyncEngine.DecideExportScopeExit(baseAccount, AccountManagedBy(baseAccount), [managerAttributes]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(ExportScopeExitOutcome.Deprovision));
            Assert.That(decision.CoveringRule, Is.Null);
        }
    }

    [Test]
    public void DecideExportScopeExit_AddOnRuleLeavesAnAccountAnotherRuleManages_LeavesTheAccountToThatRule()
    {
        // Nothing else covers the account either: the managing rule has left too, and its own exit decides.
        var baseAccount = Rule(10, "Base account", provisions: true);
        var managerAttributes = Rule(11, "Manager attributes", provisions: false, OutboundDeprovisionAction.Delete);

        var decision = SyncEngine.DecideExportScopeExit(managerAttributes, AccountManagedBy(baseAccount), []);

        Assert.That(decision.Outcome, Is.EqualTo(ExportScopeExitOutcome.ManagedByAnotherRule));
    }

    [Test]
    public void DecideExportScopeExit_AccountJoinedByAnImportRuleWhileARuleThatDoesNotProvisionCovers_KeepsTheAccount()
    {
        var leaving = Rule(10, "Leaving rule", provisions: false);
        var covering = Rule(11, "Covering rule", provisions: false);

        var decision = SyncEngine.DecideExportScopeExit(leaving, AccountJoinedByImport(), [covering]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Outcome, Is.EqualTo(ExportScopeExitOutcome.KeptByCoveringRule));
            Assert.That(decision.CoveringRule, Is.SameAs(covering));
        }
    }

    [Test]
    public void DecideExportScopeExit_AccountJoinedByAnImportRuleAndNothingCovers_Deprovisions()
    {
        var decision = SyncEngine.DecideExportScopeExit(Rule(10, "Leaving rule", provisions: false), AccountJoinedByImport(), []);

        Assert.That(decision.Outcome, Is.EqualTo(ExportScopeExitOutcome.Deprovision));
    }

    [Test]
    public void DecideExportScopeExit_AccountWhoseManagingRuleWasDeleted_IsTreatedAsManagedByNoRule()
    {
        // Deleting a rule clears the account's JoinSyncRuleId; the join method still says an export rule made it.
        var leaving = Rule(10, "Leaving rule", provisions: true);
        var covering = Rule(11, "Covering rule", provisions: false);
        var account = AccountManagedBy(leaving);
        account.JoinSyncRuleId = null;

        var decision = SyncEngine.DecideExportScopeExit(leaving, account, [covering]);

        Assert.That(decision.Outcome, Is.EqualTo(ExportScopeExitOutcome.KeptByCoveringRule));
    }

    [Test]
    public void DecideExportScopeExit_CoveringRuleForAnotherObjectType_DoesNotKeepTheAccount()
    {
        var leaving = Rule(10, "Leaving rule", provisions: true);
        var contacts = Rule(11, "Contacts", provisions: true, csoTypeId: ContactTypeId);

        var decision = SyncEngine.DecideExportScopeExit(leaving, AccountManagedBy(leaving), [contacts]);

        Assert.That(decision.Outcome, Is.EqualTo(ExportScopeExitOutcome.Deprovision));
    }

    [Test]
    public void DecideExportScopeExit_TheDepartingRuleAmongTheCandidates_IsNotItsOwnCover()
    {
        var leaving = Rule(10, "Leaving rule", provisions: true);

        var decision = SyncEngine.DecideExportScopeExit(leaving, AccountManagedBy(leaving), [leaving]);

        Assert.That(decision.Outcome, Is.EqualTo(ExportScopeExitOutcome.Deprovision));
    }

    [Test]
    public void FindExportRuleHandOver_CoveringRuleDoesNotProvision_HandsNothingOver()
    {
        var baseAccount = Rule(10, "Base account", provisions: true);
        var managerAttributes = Rule(11, "Manager attributes", provisions: false);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var account = AccountManagedBy(baseAccount, mvo.Id);

        var handOver = SyncEngine.FindExportRuleHandOver(mvo, managerAttributes, account, [baseAccount, managerAttributes], [managerAttributes]);

        Assert.That(handOver, Is.Null, "only a rule that provisions may take an account over");
    }

    [Test]
    public void FindExportRuleHandOver_CoveringRuleProvisions_HandsTheAccountOver()
    {
        var contractor = Rule(10, "Contractor accounts", provisions: true);
        var staff = Rule(11, "Staff accounts", provisions: true);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var account = AccountManagedBy(contractor, mvo.Id);

        var handOver = SyncEngine.FindExportRuleHandOver(mvo, staff, account, [contractor, staff], [staff]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handOver, Is.Not.Null);
            Assert.That(handOver?.DepartedSyncRuleId, Is.EqualTo(contractor.Id));
            Assert.That(handOver?.CoveringSyncRuleId, Is.EqualTo(staff.Id));
        }
    }

    [Test]
    public void IsLeavingWithItsManagingRule_ManagingRuleOutOfScopeAndOnlyAnAddOnCovers_IsTrue()
    {
        // The add-on rule must not stage changes for an account its managing rule is about to deprovision.
        var baseAccount = Rule(10, "Base account", provisions: true);
        var managerAttributes = Rule(11, "Manager attributes", provisions: false);

        var leaving = SyncEngine.IsLeavingWithItsManagingRule(AccountManagedBy(baseAccount), [baseAccount, managerAttributes], [managerAttributes]);

        Assert.That(leaving, Is.True);
    }

    [Test]
    public void IsLeavingWithItsManagingRule_ManagingRuleStillInScope_IsFalse()
    {
        var baseAccount = Rule(10, "Base account", provisions: true);
        var managerAttributes = Rule(11, "Manager attributes", provisions: false);

        var leaving = SyncEngine.IsLeavingWithItsManagingRule(AccountManagedBy(baseAccount), [baseAccount, managerAttributes],
            [baseAccount, managerAttributes]);

        Assert.That(leaving, Is.False);
    }

    [Test]
    public void IsLeavingWithItsManagingRule_AProvisioningRuleTakesTheAccountOver_IsFalse()
    {
        var contractor = Rule(10, "Contractor accounts", provisions: true);
        var staff = Rule(11, "Staff accounts", provisions: true);

        var leaving = SyncEngine.IsLeavingWithItsManagingRule(AccountManagedBy(contractor), [contractor, staff], [staff]);

        Assert.That(leaving, Is.False);
    }

    [Test]
    public void IsLeavingWithItsManagingRule_ManagingRuleDisabled_IsFalse()
    {
        // A disabled rule is paused: it is not among the enabled rules, and nothing deprovisions the account it manages.
        var baseAccount = Rule(10, "Base account", provisions: true);
        var managerAttributes = Rule(11, "Manager attributes", provisions: false);

        var leaving = SyncEngine.IsLeavingWithItsManagingRule(AccountManagedBy(baseAccount), [managerAttributes], [managerAttributes]);

        Assert.That(leaving, Is.False);
    }

    [Test]
    public void IsLeavingWithItsManagingRule_AccountAnImportRuleJoined_IsFalse()
    {
        var addOn = Rule(11, "Manager attributes", provisions: false);

        var leaving = SyncEngine.IsLeavingWithItsManagingRule(AccountJoinedByImport(), [addOn], [addOn]);

        Assert.That(leaving, Is.False);
    }

    [Test]
    public void ManagingExportRuleId_AccountAnImportRuleJoined_IsNull()
    {
        Assert.That(SyncEngine.ManagingExportRuleId(AccountJoinedByImport()), Is.Null);
    }

    [Test]
    public void ManagingExportRuleId_AccountAnExportRuleProvisioned_IsThatRule()
    {
        var rule = Rule(10, "Base account", provisions: true);

        Assert.That(SyncEngine.ManagingExportRuleId(AccountManagedBy(rule)), Is.EqualTo(rule.Id));
    }

    private static SyncRule Rule(int id, string name, bool provisions,
        OutboundDeprovisionAction action = OutboundDeprovisionAction.Disconnect, int csoTypeId = UserTypeId) => new()
    {
        Id = id,
        Name = name,
        ConnectedSystemId = SystemId,
        ConnectedSystemObjectTypeId = csoTypeId,
        MetaverseObjectTypeId = 100,
        Direction = SyncRuleDirection.Export,
        Enabled = true,
        ProvisionToConnectedSystem = provisions,
        OutboundDeprovisionAction = action
    };

    private static ConnectedSystemObject AccountManagedBy(SyncRule managingRule, Guid? mvoId = null)
    {
        var account = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = SystemId,
            TypeId = UserTypeId,
            MetaverseObjectId = mvoId ?? Guid.NewGuid()
        };
        account.RecordJoin(ConnectedSystemObjectJoinMethod.Provisioning, managingRule, DateTime.UtcNow);
        return account;
    }

    private static ConnectedSystemObject AccountJoinedByImport()
    {
        var importRule = new SyncRule { Id = 99, Name = "AD import", Direction = SyncRuleDirection.Import, ConnectedSystemId = SystemId };
        var account = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = SystemId,
            TypeId = UserTypeId,
            MetaverseObjectId = Guid.NewGuid()
        };
        account.RecordJoin(ConnectedSystemObjectJoinMethod.InboundMatching, importRule, DateTime.UtcNow);
        return account;
    }
}
