// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The one decision of what delivering a password may write to an account's flags: whether it is enabled (#2064) and
/// whether its password never expires (#2159), as one truth table over one ownership ladder.
/// <para>
/// The cases the table exists for are accounts whose flags an Attribute Flow writes: one the flow deliberately creates
/// disabled (a pre-hire, or a person archived at creation), which the rule's Enable Account used to enable; and one
/// the flow gives "Password never expires", which the Initial Password's expiry used to clear (and the reverse). Where
/// an export wrote the account's flags, the flow decides both, and the one thing a delivery still completes is the
/// enable JIM itself withheld until a password existed (#2039).
/// </para>
/// </summary>
[TestFixture]
public class AccountFlagsPolicyTests
{
    // Origin, administrator's enable choice, expiry asked for, flags written by an export, rule's Enable Account,
    // withheld enable held -> enabled state sent and its basis, "never expires" sent and its basis
    [TestCase(PendingPasswordChangeOrigin.Explicit, true, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, true, false, true,
        true, AccountFlagBasis.AdministratorChoice, false, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_Explicit_TheAdministratorsChoicesWinOverAFlowAndAWithheldEnable")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, false, PasswordExpiryBehaviour.NeverExpires, false, false, true,
        false, AccountFlagBasis.AdministratorChoice, true, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitDisableAndNeverExpires_AreBothTheAdministrators")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, false, true,
        true, AccountFlagBasis.CompletesWithheldEnable, false, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitWithNoEnableChoice_CompletesAWithheldEnable_AndStillAppliesTheExpiryChoice")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, PasswordExpiryBehaviour.NeverExpires, false, false, false,
        null, AccountFlagBasis.LeftAsIs, true, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitWithNoEnableChoice_LeavesTheEnabledStateButSetsNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, false, true,
        true, AccountFlagBasis.CompletesWithheldEnable, null, AccountFlagBasis.LeftAsIs,
        TestName = "Decide_Propagated_CompletesAWithheldEnable_AndLeavesNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, PasswordExpiryBehaviour.NeverExpires, false, false, false,
        null, AccountFlagBasis.LeftAsIs, null, AccountFlagBasis.LeftAsIs,
        TestName = "Decide_Propagated_QueuedWithNeverExpires_LeavesBothFlags")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, true, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false, false, false,
        null, AccountFlagBasis.LeftAsIs, null, AccountFlagBasis.LeftAsIs,
        TestName = "Decide_Propagated_IgnoresAnEnableDecisionOnTheRow")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, PasswordExpiryBehaviour.NeverExpires, true, false, true,
        true, AccountFlagBasis.CompletesWithheldEnable, null, AccountFlagBasis.LeftToAttributeFlow,
        TestName = "Decide_Provisioned_HeldEnable_EnablesAndLeavesNeverExpiresToTheFlow")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, true, true, false,
        null, AccountFlagBasis.LeftToAttributeFlow, null, AccountFlagBasis.LeftToAttributeFlow,
        TestName = "Decide_Provisioned_FlagsWrittenByAnExport_LeavesBothToTheFlow")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, PasswordExpiryBehaviour.NeverExpires, false, true, false,
        true, AccountFlagBasis.SyncRuleSetting, true, AccountFlagBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_NothingWritesTheFlags_TheRuleEnablesAndSetsNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, false, false,
        null, AccountFlagBasis.SyncRuleSetting, false, AccountFlagBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_NothingWritesTheFlags_RuleOffLeavesTheAccountAsCreatedAndClearsNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, true, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false, false, false,
        null, AccountFlagBasis.SyncRuleSetting, false, AccountFlagBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_IgnoresAnEnableDecisionOnTheRow")]
    public void Decide_ReturnsThePrecedenceTablesAnswer(
        PendingPasswordChangeOrigin origin,
        bool? administratorEnableChoice,
        PasswordExpiryBehaviour requestedExpiry,
        bool accountFlagsWrittenByExport,
        bool ruleEnableAccount,
        bool accountHoldsWithheldEnable,
        bool? expectedEnableAccount,
        AccountFlagBasis expectedEnablementBasis,
        bool? expectedPasswordNeverExpires,
        AccountFlagBasis expectedPasswordNeverExpiresBasis)
    {
        var decision = AccountFlagsPolicy.Decide(
            origin, administratorEnableChoice, requestedExpiry, accountFlagsWrittenByExport, ruleEnableAccount, accountHoldsWithheldEnable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.EnableAccount, Is.EqualTo(expectedEnableAccount));
            Assert.That(decision.EnablementBasis, Is.EqualTo(expectedEnablementBasis));
            Assert.That(decision.PasswordNeverExpires, Is.EqualTo(expectedPasswordNeverExpires));
            Assert.That(decision.PasswordNeverExpiresBasis, Is.EqualTo(expectedPasswordNeverExpiresBasis));
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
        Assert.That(AccountFlagsPolicy.DependsOnWithheldEnable(origin, administratorChoice), Is.EqualTo(expected));
    }

    // An export still in flight may be disabling the account (#2173), writing the flags it owns (#2159), or placing a
    // hold this very delivery would complete (#2158), and records any of these only once its batch has come back. Every
    // decision but an administrator's choice of the enabled state waits for it, whether it writes a flag or not.
    [TestCase(true, AccountFlagBasis.SyncRuleSetting, true, AccountFlagBasis.SyncRuleSetting, true)]
    [TestCase(true, AccountFlagBasis.CompletesWithheldEnable, null, AccountFlagBasis.LeftAsIs, true)]
    [TestCase(true, AccountFlagBasis.AdministratorChoice, false, AccountFlagBasis.AdministratorChoice, false)]
    [TestCase(false, AccountFlagBasis.AdministratorChoice, true, AccountFlagBasis.AdministratorChoice, false)]
    [TestCase(null, AccountFlagBasis.LeftAsIs, true, AccountFlagBasis.AdministratorChoice, true)]
    [TestCase(null, AccountFlagBasis.SyncRuleSetting, false, AccountFlagBasis.SyncRuleSetting, true)]
    [TestCase(null, AccountFlagBasis.SyncRuleSetting, true, AccountFlagBasis.SyncRuleSetting, true)]
    [TestCase(null, AccountFlagBasis.LeftToAttributeFlow, null, AccountFlagBasis.LeftToAttributeFlow, true)]
    [TestCase(null, AccountFlagBasis.LeftAsIs, null, AccountFlagBasis.LeftAsIs, true)]
    public void WaitsForAnExportInFlight_EveryDecisionButAnAdministratorsChoiceOfTheEnabledState(
        bool? enableAccount, AccountFlagBasis enablementBasis, bool? passwordNeverExpires, AccountFlagBasis passwordNeverExpiresBasis, bool expected)
    {
        var decision = new AccountFlagsDecision(enableAccount, enablementBasis, passwordNeverExpires, passwordNeverExpiresBasis);

        Assert.That(AccountFlagsPolicy.WaitsForAnExportInFlight(decision), Is.EqualTo(expected));
    }
}
