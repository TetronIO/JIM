// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The one-line summaries a collapsed section of the Attribute Flow dialog shows (#2471): collapsing a section must
/// never hide what it is set to.
/// </summary>
[TestFixture]
public class AttributeFlowSectionSummariesTests
{
    [TestCase(GeneratedValueTokenKind.OnlyIfTaken, "Add a number only if taken")]
    [TestCase(GeneratedValueTokenKind.Sequence, "Always add a sequence number")]
    [TestCase(GeneratedValueTokenKind.Random, "Always add a random token")]
    public void UniquenessToken_EachKind_NamesIt(GeneratedValueTokenKind kind, string expected)
    {
        Assert.That(AttributeFlowSectionSummaries.UniquenessToken(kind), Is.EqualTo(expected));
    }

    [TestCase(MissingInputBehaviour.ContributeNoValue, true, "Wait until every input has a value")]
    [TestCase(MissingInputBehaviour.EvaluateAnyway, true, "Evaluate anyway")]
    [TestCase(MissingInputBehaviour.ContributeNoValue, false, "Contribute no value")]
    [TestCase(MissingInputBehaviour.FailMapping, false, "Fail this mapping")]
    [TestCase(MissingInputBehaviour.FailObject, false, "Fail the object")]
    public void MissingInput_NamesTheChoiceAsTheFormOffersIt(MissingInputBehaviour behaviour, bool generated, string expected)
    {
        // A Generated Value offers "Contribute no value" as waiting, since its value is kept for ever once generated.
        Assert.That(AttributeFlowSectionSummaries.MissingInput(behaviour, generated), Is.EqualTo(expected));
    }

    [Test]
    public void Availability_StillLoading_SaysSo()
    {
        Assert.That(AttributeFlowSectionSummaries.Availability(null), Is.EqualTo("Loading"));
    }

    [Test]
    public void Availability_NoParticipants_SaysNothingIsChecked()
    {
        Assert.That(AttributeFlowSectionSummaries.Availability([]), Is.EqualTo("Not exported to any Connected System"));
    }

    [Test]
    public void Availability_SomeExcluded_CountsCheckedAndExcluded()
    {
        var participants = new List<GeneratedValueParticipant>
        {
            new() { ConnectedSystemName = "AD" },
            new() { ConnectedSystemName = "LDAP" },
            new() { ConnectedSystemName = "SCIM", IsExcluded = true }
        };

        Assert.That(AttributeFlowSectionSummaries.Availability(participants), Is.EqualTo("Checked in 2 Connected Systems, 1 excluded"));
    }

    [Test]
    public void Availability_OneChecked_UsesTheSingular()
    {
        Assert.That(AttributeFlowSectionSummaries.Availability([new GeneratedValueParticipant { ConnectedSystemName = "AD" }]),
            Is.EqualTo("Checked in 1 Connected System"));
    }

    [Test]
    public void CollisionRemediation_NoSystemReportsCollisions_SaysNotAvailable()
    {
        var generation = new SyncRuleMappingGeneration { CollisionRemediation = true };

        Assert.That(AttributeFlowSectionSummaries.CollisionRemediation(generation, [new GeneratedValueParticipant { ReportsCollisions = false }]),
            Is.EqualTo("Not available"), "the switch reads off while no target can report a collision, whatever is stored");
    }

    [TestCase(true, "On")]
    [TestCase(false, "Off")]
    public void CollisionRemediation_Available_SaysOnOrOff(bool on, string expected)
    {
        var generation = new SyncRuleMappingGeneration { CollisionRemediation = on };

        Assert.That(AttributeFlowSectionSummaries.CollisionRemediation(generation, [new GeneratedValueParticipant { ReportsCollisions = true }]),
            Is.EqualTo(expected));
    }

    [Test]
    public void ValueHandling_Defaults_ListsWhatIsOn()
    {
        var mapping = new SyncRuleMapping();

        Assert.That(AttributeFlowSectionSummaries.ValueHandling(mapping, textTarget: true), Is.EqualTo("Treat whitespace as no value"));
    }

    [Test]
    public void ValueHandling_EverythingSet_ListsEachInOrder()
    {
        var mapping = new SyncRuleMapping
        {
            NullIsValue = true,
            InboundValueProcessing = InboundValueProcessing.TrimWhitespace | InboundValueProcessing.CollapseInternalWhitespace,
            CaseNormalisation = InboundCaseNormalisation.Lower
        };

        Assert.That(AttributeFlowSectionSummaries.ValueHandling(mapping, textTarget: true),
            Is.EqualTo("Null is a value, Trim whitespace, Collapse internal whitespace, Lower case"));
    }

    [Test]
    public void ValueHandling_NotATextTarget_IgnoresValueProcessing()
    {
        // Value processing applies to Text attributes only, so the form offers only "Null is a value" for anything else.
        var mapping = new SyncRuleMapping { CaseNormalisation = InboundCaseNormalisation.Upper };

        Assert.That(AttributeFlowSectionSummaries.ValueHandling(mapping, textTarget: false), Is.EqualTo("Null is not a value"));
    }
}
