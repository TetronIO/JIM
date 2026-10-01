// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the Attribute Flow dialog shows from the live analysis (#1750, mockups B and C): the read-only Derived
/// Attribute Flow panel, the non-repeatable function warning, and the error the save would refuse with, the loop
/// listed link by link.
/// </summary>
[TestFixture]
public class DerivedFlowPanelTests : JimComponentTestContext
{
    private const string LoopMessage =
        "Saving would create a dependency cycle: Account Name (Synchronisation Rule 'HR Inbound') reads User Principal Name, " +
        "which (Synchronisation Rule 'HR Inbound') reads Email, which (Synchronisation Rule 'HR Inbound') reads Account Name.";

    private static DerivedFlowAnalysis Derived(IReadOnlyList<string>? warnings = null) => new()
    {
        Status = DerivedFlowAnalysisStatus.Derived,
        MetaverseInputs = ["Account Name"],
        Step = 2,
        StepCount = 3,
        Steps = [new DerivedFlowAnalysisStep(1, ["Account Name"]), new DerivedFlowAnalysisStep(2, ["Email"])],
        Warnings = warnings ?? []
    };

    private IRenderedComponent<DerivedFlowPanel> RenderPanel(DerivedFlowAnalysis? analysis) =>
        Render<DerivedFlowPanel>(p => p.Add(c => c.Analysis, analysis).Add(c => c.ConnectedSystemName, "HR"));

    [Test]
    public void DerivedFlowPanel_DerivedFlow_StatesItsStepInputsAndChain()
    {
        var cut = RenderPanel(Derived());

        var panel = cut.Find("[data-testid='jim-derived-flow-panel']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid='jim-derived-flow-panel-step']").TextContent.Trim(), Is.EqualTo("step 2 of 3"));
            Assert.That(cut.FindComponents<AttributeChip>().Select(c => (c.Instance.Kind, c.Instance.Name)),
                Does.Contain((AttributeChipKind.Metaverse, "Account Name")));
            Assert.That(panel.TextContent, Does.Contain("JIM runs it after Account Name is resolved, in HR's own synchronisation."));
            Assert.That(cut.FindAll("[data-testid='jim-derived-flow-chain-step']").Select(e => e.TextContent.Trim()),
                Is.EqualTo(new[] { "1 Account Name", "2 Email" }));
            Assert.That(cut.Find("[data-testid='jim-derived-flow-chain-current']").TextContent.Trim(), Is.EqualTo("2 Email"),
                "the step being edited is the emphasised one");
            Assert.That(panel.TextContent, Does.Contain("When another Connected System changes Account Name, JIM marks this object so HR's next synchronisation picks it up."));
        }
    }

    [Test]
    public void DerivedFlowPanel_OrdinaryFlow_ShowsNothing()
    {
        var cut = RenderPanel(new DerivedFlowAnalysis { Status = DerivedFlowAnalysisStatus.NotDerived, Step = 1, StepCount = 3 });

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void DerivedFlowPanel_NoAnalysisYet_ShowsNothing()
    {
        var cut = RenderPanel(null);

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void DerivedFlowPanel_NonRepeatableFunction_ShowsTheWarningWithoutBlocking()
    {
        const string warning = "The Attribute Flow to Email (Synchronisation Rule 'HR Inbound') derives its value from Metaverse attributes and calls Now(), which returns a different value each time it is evaluated; the value will change on every synchronisation and can cause repeated exports.";
        var cut = RenderPanel(Derived([warning]));

        var alert = cut.FindComponents<MudAlert>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alert.Instance.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(alert.Markup, Does.Contain("calls Now()"));
            Assert.That(alert.Markup, Does.Contain("You can still save it."));
        }
    }

    [Test]
    public void DerivedFlowPanel_Loop_ListsItLinkByLinkInReadingOrder()
    {
        var analysis = new DerivedFlowAnalysis
        {
            Status = DerivedFlowAnalysisStatus.Derived,
            MetaverseInputs = ["User Principal Name"],
            StepCount = 1,
            Errors = [LoopMessage],
            Cycle = new DerivedFlowAnalysisCycle(LoopMessage,
            [
                new DerivedFlowAnalysisCycleLink("Account Name", "User Principal Name", "HR Inbound", true),
                new DerivedFlowAnalysisCycleLink("User Principal Name", "Email", "HR Inbound", false),
                new DerivedFlowAnalysisCycleLink("Email", "Account Name", "HR Inbound", false)
            ])
        };

        var cut = RenderPanel(analysis);

        var alert = cut.FindComponents<MudAlert>().Single(a => a.Instance.Severity == Severity.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alert.Markup, Does.Contain("This Attribute Flow cannot be saved because it creates a loop."));
            Assert.That(alert.FindAll("li").Select(li => li.TextContent.Trim()), Is.EqualTo(new[]
            {
                "Account Name, from User Principal Name (this flow, HR Inbound)",
                "Email, from Account Name (HR Inbound)",
                "User Principal Name, from Email (HR Inbound)"
            }));
            Assert.That(alert.Markup, Does.Not.Contain("Saving would create a dependency cycle"), "the list stands for the message");
        }
    }

    [Test]
    public void DerivedFlowPanel_ErrorThatIsNotALoop_ShowsTheSavesMessage()
    {
        const string unknown = "The Attribute Flow to Email (Synchronisation Rule 'HR Inbound') reads mv[\"Acount Name\"], but 'Acount Name' is not an attribute of the Metaverse Object Type 'User'.";
        var cut = RenderPanel(new DerivedFlowAnalysis
        {
            Status = DerivedFlowAnalysisStatus.Derived,
            MetaverseInputs = ["Acount Name"],
            StepCount = 2,
            Errors = [unknown]
        });

        var alert = cut.FindComponents<MudAlert>().Single(a => a.Instance.Severity == Severity.Error);
        Assert.That(alert.Find("[data-testid='jim-derived-flow-error']").TextContent.Trim(), Is.EqualTo(unknown));
    }
}
