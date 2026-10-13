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
    // Origin, administrator's enable choice, expiry asked for, flags written by an export, the first password's account
    // flags (the provisioning rule's Enable Account and expiry, or null where the delivery is no account's first
    // password), withheld enable held -> enabled state sent and its basis, "never expires" sent and its basis
    [TestCase(PendingPasswordChangeOrigin.Explicit, true, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, true, null, null, true,
        true, AccountFlagBasis.AdministratorChoice, false, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_Explicit_TheAdministratorsChoicesWinOverAFlowAndAWithheldEnable")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, false, PasswordExpiryBehaviour.NeverExpires, false, null, null, true,
        false, AccountFlagBasis.AdministratorChoice, true, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitDisableAndNeverExpires_AreBothTheAdministrators")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, null, null, true,
        true, AccountFlagBasis.CompletesWithheldEnable, false, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitWithNoEnableChoice_CompletesAWithheldEnable_AndStillAppliesTheExpiryChoice")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, PasswordExpiryBehaviour.NeverExpires, false, null, null, false,
        null, AccountFlagBasis.LeftAsIs, true, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitWithNoEnableChoice_LeavesTheEnabledStateButSetsNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, null, null, true,
        true, AccountFlagBasis.CompletesWithheldEnable, null, AccountFlagBasis.LeftAsIs,
        TestName = "Decide_Propagated_CompletesAWithheldEnable_AndLeavesNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, PasswordExpiryBehaviour.NeverExpires, false, null, null, false,
        null, AccountFlagBasis.LeftAsIs, null, AccountFlagBasis.LeftAsIs,
        TestName = "Decide_Propagated_QueuedWithNeverExpires_LeavesBothFlags")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, true, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false, null, null, false,
        null, AccountFlagBasis.LeftAsIs, null, AccountFlagBasis.LeftAsIs,
        TestName = "Decide_Propagated_IgnoresAnEnableDecisionOnTheRow")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, PasswordExpiryBehaviour.NeverExpires, true, false, PasswordExpiryBehaviour.NeverExpires, true,
        true, AccountFlagBasis.CompletesWithheldEnable, null, AccountFlagBasis.LeftToAttributeFlow,
        TestName = "Decide_Provisioned_HeldEnable_EnablesAndLeavesNeverExpiresToTheFlow")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, true, true, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false,
        null, AccountFlagBasis.LeftToAttributeFlow, null, AccountFlagBasis.LeftToAttributeFlow,
        TestName = "Decide_Provisioned_FlagsWrittenByAnExport_LeavesBothToTheFlow")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, PasswordExpiryBehaviour.NeverExpires, false, true, PasswordExpiryBehaviour.NeverExpires, false,
        true, AccountFlagBasis.SyncRuleSetting, true, AccountFlagBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_NothingWritesTheFlags_TheRuleEnablesAndSetsNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, false, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false,
        null, AccountFlagBasis.SyncRuleSetting, false, AccountFlagBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_NothingWritesTheFlags_RuleOffLeavesTheAccountAsCreatedAndClearsNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, true, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false, false, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false,
        null, AccountFlagBasis.SyncRuleSetting, false, AccountFlagBasis.SyncRuleSetting,
        TestName = "Decide_Provisioned_IgnoresAnEnableDecisionOnTheRow")]
    // A password change delivered as the account's first password in place of its Initial Password (#2160) carries the
    // Initial Password's promises about the account, under the same ladder: never its password, never its "must change
    // at next sign-in", and never over the export that writes userAccountControl.
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, true, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false,
        true, AccountFlagBasis.SyncRuleSetting, false, AccountFlagBasis.SyncRuleSetting,
        TestName = "Decide_PropagatedFirstPassword_RuleOn_EnablesAndClearsNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, false, PasswordExpiryBehaviour.NeverExpires, false,
        null, AccountFlagBasis.SyncRuleSetting, true, AccountFlagBasis.SyncRuleSetting,
        TestName = "Decide_PropagatedFirstPassword_RuleOff_LeavesItAsCreatedAndSetsNeverExpires")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, true, true, PasswordExpiryBehaviour.NeverExpires, false,
        null, AccountFlagBasis.LeftToAttributeFlow, null, AccountFlagBasis.LeftToAttributeFlow,
        TestName = "Decide_PropagatedFirstPassword_FlagsWrittenByAnExport_LeavesBothToTheFlow")]
    [TestCase(PendingPasswordChangeOrigin.Propagated, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, true, false, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, true,
        true, AccountFlagBasis.CompletesWithheldEnable, null, AccountFlagBasis.LeftToAttributeFlow,
        TestName = "Decide_PropagatedFirstPassword_HeldEnable_Completes")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false, true, PasswordExpiryBehaviour.NeverExpires, false,
        true, AccountFlagBasis.SyncRuleSetting, false, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitFirstPasswordWithNoEnableChoice_FollowsTheRulesEnableAndKeepsTheAdministratorsExpiry")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, null, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, true, true, PasswordExpiryBehaviour.NeverExpires, false,
        null, AccountFlagBasis.LeftToAttributeFlow, false, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitFirstPasswordWithNoEnableChoice_FlagsWrittenByAnExport_LeavesTheEnabledStateToTheFlow")]
    [TestCase(PendingPasswordChangeOrigin.Explicit, true, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, false, PasswordExpiryBehaviour.NeverExpires, false,
        true, AccountFlagBasis.AdministratorChoice, false, AccountFlagBasis.AdministratorChoice,
        TestName = "Decide_ExplicitFirstPasswordWithAChoice_TheChoiceWins")]
    public void Decide_ReturnsThePrecedenceTablesAnswer(
        PendingPasswordChangeOrigin origin,
        bool? administratorEnableChoice,
        PasswordExpiryBehaviour requestedExpiry,
        bool accountFlagsWrittenByExport,
        bool? ruleEnableAccount,
        PasswordExpiryBehaviour? ruleExpiry,
        bool accountHoldsWithheldEnable,
        bool? expectedEnableAccount,
        AccountFlagBasis expectedEnablementBasis,
        bool? expectedPasswordNeverExpires,
        AccountFlagBasis expectedPasswordNeverExpiresBasis)
    {
        var firstPassword = ruleEnableAccount is { } enable && ruleExpiry is { } expiry
            ? new InitialPasswordAccountFlags(enable, expiry)
            : null;

        var decision = AccountFlagsPolicy.Decide(
            origin, administratorEnableChoice, requestedExpiry, accountFlagsWrittenByExport, firstPassword, accountHoldsWithheldEnable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.EnableAccount, Is.EqualTo(expectedEnableAccount));
            Assert.That(decision.EnablementBasis, Is.EqualTo(expectedEnablementBasis));
            Assert.That(decision.PasswordNeverExpires, Is.EqualTo(expectedPasswordNeverExpires));
            Assert.That(decision.PasswordNeverExpiresBasis, Is.EqualTo(expectedPasswordNeverExpiresBasis));
        }
    }

    // The expiry the Connector is sent must agree with "Password never expires" wherever the decision writes the flag,
    // because the Connector derives the flag from it. A first password carried by a propagated change keeps that
    // password's own expiry ("must change at next sign-in" belongs to the password), and takes Never expires only from
    // the account's promise (#2160).
    [TestCase(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, true, PasswordExpiryBehaviour.NeverExpires)]
    [TestCase(PasswordExpiryBehaviour.RequireChangeAtNextSignIn, true, PasswordExpiryBehaviour.NeverExpires)]
    [TestCase(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, false, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy)]
    [TestCase(PasswordExpiryBehaviour.RequireChangeAtNextSignIn, false, PasswordExpiryBehaviour.RequireChangeAtNextSignIn)]
    [TestCase(PasswordExpiryBehaviour.NeverExpires, false, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy)]
    [TestCase(PasswordExpiryBehaviour.NeverExpires, null, PasswordExpiryBehaviour.NeverExpires)]
    [TestCase(PasswordExpiryBehaviour.RequireChangeAtNextSignIn, null, PasswordExpiryBehaviour.RequireChangeAtNextSignIn)]
    public void ExpiryToSend_AgreesWithTheDecisionWhereItWritesTheFlag(
        PasswordExpiryBehaviour passwordExpiry, bool? passwordNeverExpires, PasswordExpiryBehaviour expected)
    {
        var decision = new AccountFlagsDecision(null, AccountFlagBasis.LeftAsIs, passwordNeverExpires,
            passwordNeverExpires == null ? AccountFlagBasis.LeftAsIs : AccountFlagBasis.SyncRuleSetting);

        Assert.That(AccountFlagsPolicy.ExpiryToSend(passwordExpiry, decision), Is.EqualTo(expected));
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

    // An export still in flight may be about to disable the account, and records that it wrote the account's flags only
    // once it has finished (#2173). JIM's own decision to enable waits for it, and so does JIM's own write of "Password
    // never expires" from the Synchronisation Rule (#2159): the export may be the flow that owns the flag. An
    // administrator's choice does not wait, and a decision that writes nothing has nothing to wait for.
    [TestCase(true, AccountFlagBasis.SyncRuleSetting, true, AccountFlagBasis.SyncRuleSetting, true)]
    [TestCase(true, AccountFlagBasis.CompletesWithheldEnable, null, AccountFlagBasis.LeftAsIs, true)]
    [TestCase(true, AccountFlagBasis.AdministratorChoice, false, AccountFlagBasis.AdministratorChoice, false)]
    [TestCase(false, AccountFlagBasis.AdministratorChoice, true, AccountFlagBasis.AdministratorChoice, false)]
    [TestCase(null, AccountFlagBasis.LeftAsIs, true, AccountFlagBasis.AdministratorChoice, false)]
    [TestCase(null, AccountFlagBasis.SyncRuleSetting, false, AccountFlagBasis.SyncRuleSetting, true)]
    [TestCase(null, AccountFlagBasis.SyncRuleSetting, true, AccountFlagBasis.SyncRuleSetting, true)]
    [TestCase(null, AccountFlagBasis.LeftToAttributeFlow, null, AccountFlagBasis.LeftToAttributeFlow, false)]
    [TestCase(null, AccountFlagBasis.LeftAsIs, null, AccountFlagBasis.LeftAsIs, false)]
    public void WaitsForAnExportInFlight_OnlyWhereJimItselfWouldWriteTheFlags(
        bool? enableAccount, AccountFlagBasis enablementBasis, bool? passwordNeverExpires, AccountFlagBasis passwordNeverExpiresBasis, bool expected)
    {
        var decision = new AccountFlagsDecision(enableAccount, enablementBasis, passwordNeverExpires, passwordNeverExpiresBasis);

        Assert.That(AccountFlagsPolicy.WaitsForAnExportInFlight(decision), Is.EqualTo(expected));
    }
}
