// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The precedence that decides whether delivering a password enables, disables or leaves an account alone (#2064),
/// as one truth table.
/// <para>
/// The case the table exists for is an account an Attribute Flow deliberately creates disabled (a pre-hire, or a
/// person archived at creation): the provisioning Synchronisation Rule's Enable Account used to enable it with a
/// password JIM knows. Where an export wrote the account's enabled state, the flow now decides, and the one thing a
/// delivery still completes is the enable JIM itself withheld until a password existed (#2039).
/// </para>
/// </summary>
[TestFixture]
public class PasswordEnablementPolicyTests
{
    // Origin, administrator's choice, set by an Attribute Flow, rule's Enable Account, withheld enable held
    //   -> sends, basis
    [TestCase(PendingPasswordChangeOrigin.Explicit, true, true, false, true, true, AccountEnablementBasis.AdministratorChoice,
        TestName = "Decide_ExplicitEnable_WinsOverEverythingElse")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, false, false, false, true, false, AccountEnablementBasis.AdministratorChoice,
        TestName = "Decide_ExplicitDisable_WinsOverAWithheldEnable")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, false, false, true, true, AccountEnablementBasis.CompletesWithheldEnable,
        TestName = "Decide_ExplicitWithNoChoice_CompletesAWithheldEnable")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, false, false, false, null, AccountEnablementBasis.LeftAsIs,
        TestName = "Decide_ExplicitWithNoChoice_LeavesTheAccountAsItIs")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, false, false, true, true, AccountEnablementBasis.CompletesWithheldEnable,
        TestName = "Decide_Propagated_CompletesAWithheldEnable")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, false, false, false, null, AccountEnablementBasis.LeftAsIs,
        TestName = "Decide_Propagated_NeverEnablesOtherwise")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, true, false, false, false, null, AccountEnablementBasis.LeftAsIs,
        TestName = "Decide_Propagated_IgnoresAnEnableDecisionOnTheRow")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, true, false, true, true, AccountEnablementBasis.CompletesWithheldEnable,
        TestName = "Decide_Provisioned_HeldEnable_EnablesWhateverTheRuleSays")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, true, true, false, null, AccountEnablementBasis.LeftToAttributeFlow,
        TestName = "Decide_Provisioned_EnabledStateSetByAttributeFlow_LeavesItToTheFlow")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, false, true, false, true, AccountEnablementBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_NoFlowSetsTheEnabledState_RuleOnEnables")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, false, false, false, null, AccountEnablementBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_NoFlowSetsTheEnabledState_RuleOffLeavesTheAccountAsCreated")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, true, false, false, false, null, AccountEnablementBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_IgnoresAnEnableDecisionOnTheRow")]
    public void Decide_ReturnsThePrecedenceTablesAnswer(
        PendingPasswordChangeOrigin origin,
        bool? administratorChoice,
        bool enabledStateSetByAttributeFlow,
        bool ruleEnableAccount,
        bool accountHoldsWithheldEnable,
        bool? expectedEnableAccount,
        AccountEnablementBasis expectedBasis)
    {
        var decision = PasswordEnablementPolicy.Decide(
            origin, administratorChoice, enabledStateSetByAttributeFlow, ruleEnableAccount, accountHoldsWithheldEnable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.EnableAccount, Is.EqualTo(expectedEnableAccount));
            Assert.That(decision.Basis, Is.EqualTo(expectedBasis));
        }
    }

    [TestCase(PendingPasswordChangeOrigin.Explicit, true, false)]
    [TestCase(PendingPasswordChangeOrigin.Explicit, false, false)]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, true)]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, true)]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, true)]
    public void DependsOnWithheldEnable_IsFalseOnlyWhenAnAdministratorHasDecided(
        PendingPasswordChangeOrigin origin, bool? administratorChoice, bool expected)
    {
        Assert.That(PasswordEnablementPolicy.DependsOnWithheldEnable(origin, administratorChoice), Is.EqualTo(expected));
    }

    // An export still in flight may be disabling the account (#2173), or placing a hold this very delivery would
    // complete (#2158), and records either only once its batch has come back. Every decision but an administrator's
    // choice waits for it, whether it enables the account or not; an administrator's choice never waits.
    [TestCase(true, AccountEnablementBasis.SyncRuleSetting, true)]
    [TestCase(null, AccountEnablementBasis.SyncRuleSetting, true)]
    [TestCase(true, AccountEnablementBasis.CompletesWithheldEnable, true)]
    [TestCase(null, AccountEnablementBasis.LeftToAttributeFlow, true)]
    [TestCase(null, AccountEnablementBasis.LeftAsIs, true)]
    [TestCase(true, AccountEnablementBasis.AdministratorChoice, false)]
    [TestCase(false, AccountEnablementBasis.AdministratorChoice, false)]
    public void WaitsForAnExportInFlight_EveryDecisionButAnAdministratorsChoice(
        bool? enableAccount, AccountEnablementBasis basis, bool expected)
    {
        var decision = new AccountEnablementDecision(enableAccount, basis);

        Assert.That(PasswordEnablementPolicy.WaitsForAnExportInFlight(decision), Is.EqualTo(expected));
    }
}
