// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application.Expressions;
using JIM.Models.Interfaces;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Component tests for the Expression tester (#1405), which lets an administrator see what an Expression produces
/// before saving it. The real evaluator is registered rather than a stub: what is under test is the wiring between
/// the inputs the component resolves, the sample values it collects and the output it shows, and a stub evaluator
/// would let a wiring fault pass by returning whatever the test expected.
/// </summary>
[TestFixture]
public class ExpressionTesterTests : JimComponentTestContext
{
    [SetUp]
    public void SetUp()
    {
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    /// <summary>
    /// Clicks Run test by its label. A field rendered with Clearable also renders a button, and it comes first in
    /// the markup, so finding "the button" clears an input instead of running anything.
    /// </summary>
    private static void ClickRunTest(IRenderedComponent<ExpressionTester> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Run test")).Click();

    [Test]
    public void ExpressionTester_NoExpression_RendersNothing()
    {
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression, ""));

        Assert.That(cut.Markup.Trim(), Is.Empty,
            "an empty Expression has nothing to test, so the tester must not take up room in the dialog");
    }

    [Test]
    public void ExpressionTester_ExpressionReadingTwoAttributes_OffersAFieldForEach()
    {
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression,
            "Lower(cs[\"firstName\"]) + \".\" + Lower(cs[\"lastName\"])"));

        var labels = cut.FindComponents<MudTextField<string>>().Select(f => f.Instance.Label).ToList();

        Assert.That(labels, Is.EquivalentTo(new[] { "cs[\"firstName\"]", "cs[\"lastName\"]" }),
            "one field per attribute the Expression reads, labelled as the Expression addresses it");
    }

    [Test]
    public async Task ExpressionTester_WithSampleValues_ShowsTheOutputAsync()
    {
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression,
            "Lower(cs[\"firstName\"]) + \".\" + Lower(cs[\"lastName\"]) + \"@corp.local\""));

        var fields = cut.FindComponents<MudTextField<string>>();
        // Through the renderer's dispatcher, as a real edit arrives: invoking the callback directly throws
        // "The current thread is not associated with the Dispatcher".
        await cut.InvokeAsync(() => fields.Single(f => f.Instance.Label == "cs[\"firstName\"]").Instance.ValueChanged.InvokeAsync("Ada"));
        await cut.InvokeAsync(() => fields.Single(f => f.Instance.Label == "cs[\"lastName\"]").Instance.ValueChanged.InvokeAsync("Lovelace"));
        ClickRunTest(cut);

        Assert.That(cut.Markup, Does.Contain("ada.lovelace@corp.local"));
    }

    [Test]
    public void ExpressionTester_AnEmptyInput_IsTestedAsAbsentRatherThanAsAnEmptyString()
    {
        // The whole reason to have this beside the Expression field is seeing what happens when an object has no
        // value for an input. An empty box must therefore reach the evaluator as no value, which is what produces
        // the degenerate output worth catching; passing "" would test something the administrator never asked about.
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression,
            "IIF(IsNullOrEmpty(cs[\"nickname\"]), \"no nickname\", cs[\"nickname\"])"));

        ClickRunTest(cut);

        Assert.That(cut.Markup, Does.Contain("no nickname"));
    }

    [Test]
    public void ExpressionTester_ExpressionEdited_DiscardsThePreviousOutput()
    {
        // An output left standing beside a changed Expression reads as the answer to the question now on screen.
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression, "\"first answer\""));
        ClickRunTest(cut);
        Assert.That(cut.Markup, Does.Contain("first answer"));

        cut.Render(p => p.Add(c => c.Expression, "\"second answer\""));

        Assert.That(cut.Markup, Does.Not.Contain("first answer"),
            "the previous output belongs to the previous Expression and must not survive it");
    }

    [Test]
    public void ExpressionTester_ExpressionThatCannotBeParsed_ShowsTheReason()
    {
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression, "cs[\"unclosed\""));

        ClickRunTest(cut);

        Assert.That(cut.HasComponent<MudAlert>(), Is.True,
            "a malformed Expression must say so here rather than at the next Synchronisation");
    }
}
