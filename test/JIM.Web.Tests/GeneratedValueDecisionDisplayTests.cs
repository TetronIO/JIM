// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the Generated Values surfaces say about a held value under which condition (Unique Value Generation, #242,
/// release 4, Phase 9): which systems the "why it is held" sentence names for each reason, when a second line explains
/// it, and when the row offers its actions. Asserts on the systems named and on presence, not on wording.
/// </summary>
[TestFixture]
public class GeneratedValueDecisionDisplayTests
{
    private const int CorporateAd = 2;
    private const int ContractorLdap = 3;

    private static GeneratedValueDecisionHeader Held(GeneratedValueNeedsDecisionReason? reason, int? anchoredBy = CorporateAd, string? anchoredByName = "Corporate AD") => new()
    {
        AssignmentId = Guid.NewGuid(),
        Status = GeneratedValueDecisionStatus.NeedsDecision,
        AttributeName = "Account Name",
        Value = "r.okafor",
        Reason = reason,
        RejectedByConnectedSystemId = ContractorLdap,
        RejectedByConnectedSystemName = "Contractor LDAP",
        AnchoredByConnectedSystemId = anchoredBy,
        AnchoredByConnectedSystemName = anchoredByName,
        RemediationCount = reason == GeneratedValueNeedsDecisionReason.RemediationLimitReached ? 5 : 0
    };

    private static List<int?> SystemsNamed(GeneratedValueDecisionHeader decision) =>
        GeneratedValueDecisionDisplay.Why(decision).Where(p => p.IsConnectedSystem).Select(p => p.ConnectedSystemId).ToList();

    [Test]
    public void Why_AnchoredElsewhere_NamesTheRejectingThenTheAnchoringSystemAndExplainsTheRename()
    {
        var decision = Held(GeneratedValueNeedsDecisionReason.AnchoredElsewhere);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SystemsNamed(decision), Is.EqualTo(new int?[] { ContractorLdap, CorporateAd }));
            Assert.That(GeneratedValueDecisionDisplay.WhySecondary(decision), Is.Not.Null);
        }
    }

    [Test]
    public void Why_CannotTell_NamesBothSystemsAndExplainsWhyNotWithTheAnchoringSystemsName()
    {
        var decision = Held(GeneratedValueNeedsDecisionReason.CannotTell);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SystemsNamed(decision), Is.EqualTo(new int?[] { ContractorLdap, CorporateAd }));
            Assert.That(GeneratedValueDecisionDisplay.WhySecondary(decision), Does.Contain("Corporate AD"));
        }
    }

    [Test]
    public void Why_RemediationLimitReached_NamesOnlyTheRejectingSystemAndCarriesTheCount()
    {
        var decision = Held(GeneratedValueNeedsDecisionReason.RemediationLimitReached, anchoredBy: null, anchoredByName: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SystemsNamed(decision), Is.EqualTo(new int?[] { ContractorLdap }));
            Assert.That(string.Concat(GeneratedValueDecisionDisplay.Why(decision).Select(p => p.Text)), Does.Contain("5"));
            Assert.That(GeneratedValueDecisionDisplay.WhySecondary(decision), Is.Not.Null);
        }
    }

    [Test]
    public void Why_NoValueAvailable_NamesTheRejectingSystemAndSaysWhatToChange()
    {
        var decision = Held(GeneratedValueNeedsDecisionReason.NoValueAvailable, anchoredBy: null, anchoredByName: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SystemsNamed(decision), Is.EqualTo(new int?[] { ContractorLdap }));
            Assert.That(GeneratedValueDecisionDisplay.WhySecondary(decision), Is.Not.Null);
        }
    }

    [Test]
    public void Why_NoReasonRecorded_NamesTheRejectingSystemWithNoSecondLine()
    {
        var decision = Held(null, anchoredBy: null, anchoredByName: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SystemsNamed(decision), Is.EqualTo(new int?[] { ContractorLdap }));
            Assert.That(GeneratedValueDecisionDisplay.WhySecondary(decision), Is.Null);
        }
    }

    [Test]
    public void Why_RejectingSystemDeleted_SaysSoInPlainTextRatherThanAChip()
    {
        var decision = Held(GeneratedValueNeedsDecisionReason.AnchoredElsewhere);
        decision.RejectedByConnectedSystemName = null;

        var parts = GeneratedValueDecisionDisplay.Why(decision);

        Assert.That(parts.Where(p => p.IsConnectedSystem).Select(p => p.ConnectedSystemId), Is.EqualTo(new int?[] { CorporateAd }),
            "a deleted system has nowhere to link to, so it is not a chip");
    }

    [Test]
    public void OffersActions_OnlyForAValueWaitingOnADecision()
    {
        var held = Held(GeneratedValueNeedsDecisionReason.AnchoredElsewhere);
        var allowed = Held(GeneratedValueNeedsDecisionReason.AnchoredElsewhere);
        allowed.Status = GeneratedValueDecisionStatus.RenameAllowed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueDecisionDisplay.OffersActions(held), Is.True);
            Assert.That(GeneratedValueDecisionDisplay.OffersActions(allowed), Is.False, "an allowed rename waits for the export, not for anyone");
        }
    }
}
