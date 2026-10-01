// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// The sentences the portal says about Metaverse-Derived Attribute Flows (#1750): the Derived chip and its tooltip, the
/// loop list, and the dependants confirmation. Administrators see "step N of M", never the underlying level.
/// </summary>
[TestFixture]
public class DerivedFlowWordingTests
{
    [TestCase(new[] { "Account Name" }, "and", "Account Name")]
    [TestCase(new[] { "First Name", "Last Name" }, "and", "First Name and Last Name")]
    [TestCase(new[] { "A", "B", "C" }, "or", "A, B or C")]
    public void JoinNames_JoinsAsProse(string[] names, string conjunction, string expected)
    {
        Assert.That(DerivedFlowWording.JoinNames(names, conjunction), Is.EqualTo(expected));
    }

    [Test]
    public void ChipLabel_OrderedFlow_NamesItsStep()
    {
        Assert.That(DerivedFlowWording.ChipLabel(new DerivedFlowStepInfo(2, 3, ["Account Name"])), Is.EqualTo("Derived · step 2"));
    }

    [Test]
    public void ChipLabel_FlowThatCannotBeOrdered_SaysDerivedOnly()
    {
        Assert.That(DerivedFlowWording.ChipLabel(new DerivedFlowStepInfo(null, 3, ["Email"])), Is.EqualTo("Derived"));
    }

    [Test]
    public void ChipTooltip_OneInput_ReadsAsTheMockup()
    {
        Assert.That(DerivedFlowWording.ChipTooltip(new DerivedFlowStepInfo(2, 3, ["Account Name"])),
            Is.EqualTo("Reads Account Name from the Metaverse. Runs in step 2 of 3, after Account Name is resolved."));
    }

    [Test]
    public void ChipTooltip_SeveralInputs_AgreesInNumber()
    {
        Assert.That(DerivedFlowWording.ChipTooltip(new DerivedFlowStepInfo(2, 2, ["First Name", "Last Name"])),
            Is.EqualTo("Reads First Name and Last Name from the Metaverse. Runs in step 2 of 2, after First Name and Last Name are resolved."));
    }

    [Test]
    public void ChipTooltip_FlowOnALoop_SaysItCannotBeOrdered()
    {
        Assert.That(DerivedFlowWording.ChipTooltip(new DerivedFlowStepInfo(null, 2, ["Email"])),
            Is.EqualTo("Reads Email from the Metaverse. It has no step because it is part of a loop of Attribute Flows that read each other."));
    }

    [Test]
    public void CycleLinksInReadingOrder_ThreeLinks_EachReadsTheOneBeforeIt()
    {
        // As the server orders them: each link reads the attribute the NEXT link writes.
        var cycle = new DerivedFlowAnalysisCycle("message",
        [
            new DerivedFlowAnalysisCycleLink("Account Name", "User Principal Name", "HR Inbound", true),
            new DerivedFlowAnalysisCycleLink("User Principal Name", "Email", "HR Inbound", false),
            new DerivedFlowAnalysisCycleLink("Email", "Account Name", "HR Inbound", false)
        ]);

        var lines = DerivedFlowWording.CycleLinksInReadingOrder(cycle).Select(DerivedFlowWording.DescribeCycleLink);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "Account Name, from User Principal Name (this flow, HR Inbound)",
            "Email, from Account Name (HR Inbound)",
            "User Principal Name, from Email (HR Inbound)"
        }));
    }

    [Test]
    public void DescribeReads_DirectInput_IsItsName()
    {
        Assert.That(DerivedFlowWording.DescribeReads(new DependentDerivedFlowInput { MetaverseAttributeName = "Account Name" }),
            Is.EqualTo("Account Name"));
    }

    [Test]
    public void DescribeReads_IndirectInput_NamesTheAttributeItReadsAndTheChain()
    {
        var input = new DependentDerivedFlowInput { MetaverseAttributeName = "Account Name", Indirect = true, Via = ["Email"] };

        Assert.That(DerivedFlowWording.DescribeReads(input), Is.EqualTo("Email (via Account Name)"));
    }

    [Test]
    public void DependantsIntro_OneLostInput_ReadsAsTheMockup()
    {
        var dependants = new List<DependentDerivedFlow>
        {
            Dependant("Email", new DependentDerivedFlowInput { MetaverseAttributeName = "Account Name" }),
            Dependant("User Principal Name", new DependentDerivedFlowInput { MetaverseAttributeName = "Account Name", Indirect = true, Via = ["Email"] })
        };

        Assert.That(DerivedFlowWording.DependantsIntro("remove the Attribute Flow to Account Name", dependants),
            Is.EqualTo("After you remove the Attribute Flow to Account Name, nothing else contributes Account Name. " +
                       "These Derived Attribute Flows read it, so they will have no input:"));
    }

    [Test]
    public void DependantsIntro_SeveralLostInputs_NamesThemAll()
    {
        var dependants = new List<DependentDerivedFlow>
        {
            Dependant("Display Name", new DependentDerivedFlowInput { MetaverseAttributeName = "First Name" }, new DependentDerivedFlowInput { MetaverseAttributeName = "Last Name" })
        };

        Assert.That(DerivedFlowWording.DependantsIntro("disable this Synchronisation Rule", dependants),
            Is.EqualTo("After you disable this Synchronisation Rule, nothing else contributes First Name or Last Name. " +
                       "This Derived Attribute Flow reads them, so it will have no input:"));
    }

    private static DependentDerivedFlow Dependant(string target, params DependentDerivedFlowInput[] inputs) => new()
    {
        MappingId = 1,
        TargetMetaverseAttributeName = target,
        SyncRuleId = 1,
        SyncRuleName = "HR Inbound",
        ConnectedSystemId = 1,
        ConnectedSystemName = "HR",
        MissingInputs = [.. inputs]
    };
}
