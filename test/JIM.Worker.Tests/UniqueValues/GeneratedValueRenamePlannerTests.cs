// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// What the "Allow the rename" confirmation names (Unique Value Generation, #242, release 4, Phase 9): every Connected
/// System the held value is exported to where the object has an account, and whether that account is renamed, created or
/// updated; and the value JIM is likely to choose, where that is cheap to say.
/// </summary>
[TestFixture]
public class GeneratedValueRenamePlannerTests
{
    private const int CorporateAd = 2;
    private const int ContractorLdap = 3;
    private const int PayrollFeed = 5;

    private static GeneratedValueParticipant Participant(int systemId, string name, int attributeId) => new()
    {
        ConnectedSystemId = systemId,
        ConnectedSystemName = name,
        ConnectorName = "JIM LDAP Connector",
        ConnectedSystemObjectTypeAttributeId = attributeId,
        AttributeName = "sAMAccountName"
    };

    private static ConnectedSystemObject Account(int systemId, ConnectedSystemObjectStatus status, int? attributeId = null, string? value = null)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = systemId, Status = status };
        if (attributeId.HasValue)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = attributeId.Value, StringValue = value });
        return cso;
    }

    [Test]
    public void Plan_AccountHoldingTheValue_IsRenamedAndAccountBeingProvisionedIsCreated()
    {
        var participants = new[] { Participant(CorporateAd, "Corporate AD", 21), Participant(ContractorLdap, "Contractor LDAP", 31) };
        var accounts = new[]
        {
            Account(CorporateAd, ConnectedSystemObjectStatus.Normal, 21, "R.Okafor"),
            Account(ContractorLdap, ConnectedSystemObjectStatus.PendingProvisioning, 31, "r.okafor")
        };

        var plan = GeneratedValueRenamePlanner.Plan(participants, accounts, "r.okafor");

        Assert.That(plan.Select(c => (c.ConnectedSystemId, c.Kind)), Is.EqualTo(new[]
        {
            (CorporateAd, GeneratedValueRenameChangeKind.Rename),
            (ContractorLdap, GeneratedValueRenameChangeKind.Create)
        }), "holding the value is matched case-insensitively; a provisioning account is created whatever it holds");
    }

    [Test]
    public void Plan_JoinedAccountNotHoldingTheValue_IsUpdated()
    {
        var plan = GeneratedValueRenamePlanner.Plan(
            [Participant(PayrollFeed, "Payroll Feed", 51)],
            [Account(PayrollFeed, ConnectedSystemObjectStatus.Normal, 51, "someone.else")],
            "r.okafor");

        Assert.That(plan.Single().Kind, Is.EqualTo(GeneratedValueRenameChangeKind.Update));
    }

    [Test]
    public void Plan_SystemWithNoAccountOrOnlyAnObsoleteOne_IsNotNamed()
    {
        var plan = GeneratedValueRenamePlanner.Plan(
            [Participant(CorporateAd, "Corporate AD", 21), Participant(PayrollFeed, "Payroll Feed", 51)],
            [Account(PayrollFeed, ConnectedSystemObjectStatus.Obsolete, 51, "r.okafor")],
            "r.okafor");

        Assert.That(plan, Is.Empty, "nothing changes in a system where the object has no live account");
    }

    [Test]
    public void Plan_ValueExportedToTwoAttributesOfOneSystem_NamesTheSystemOnceWithTheStrongestChange()
    {
        var plan = GeneratedValueRenamePlanner.Plan(
            [Participant(CorporateAd, "Corporate AD", 21), Participant(CorporateAd, "Corporate AD", 22)],
            [Account(CorporateAd, ConnectedSystemObjectStatus.Normal, 22, "r.okafor")],
            "r.okafor");

        Assert.That(plan.Select(c => c.Kind), Is.EqualTo(new[] { GeneratedValueRenameChangeKind.Rename }));
    }

    [Test]
    public void LikelyNextValue_OnlyIfTakenFromTheBase_IsTheFirstSuffix()
    {
        var generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken, SuffixStyle = GeneratedValueSuffixStyle.Number, SuffixStart = 1 };

        Assert.That(GeneratedValueRenamePlanner.LikelyNextValue(generation, "r.okafor", "r.okafor"), Is.EqualTo("r.okafor1"));
    }

    [Test]
    public void LikelyNextValue_OnlyIfTakenAlreadySuffixed_IsTheSuffixAfterIt()
    {
        var generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken, SuffixStyle = GeneratedValueSuffixStyle.Number, SuffixStart = 1 };

        Assert.That(GeneratedValueRenamePlanner.LikelyNextValue(generation, "l.chen", "l.chen5"), Is.EqualTo("l.chen6"));
    }

    [Test]
    public void LikelyNextValue_NoBaseOrNotOnlyIfTakenOrValueNotFromTheBase_IsNotPredicted()
    {
        var onlyIfTaken = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
        var sequence = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueRenamePlanner.LikelyNextValue(onlyIfTaken, null, "r.okafor"), Is.Null);
            Assert.That(GeneratedValueRenamePlanner.LikelyNextValue(sequence, "emp", "emp0042"), Is.Null);
            Assert.That(GeneratedValueRenamePlanner.LikelyNextValue(onlyIfTaken, "r.okafor", "someone.else"), Is.Null);
        }
    }
}
