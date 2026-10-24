// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Expressions;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using Moq;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
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
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;

    [SetUp]
    public void SetUp()
    {
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();

        // Loading values from an object reads through the application, as the portal does.
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }

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

        Assert.That(cut.Markup, Does.Contain("no nickname"));
    }

    [Test]
    public void ExpressionTester_ExpressionEdited_ShowsTheNewOutputInPlaceOfThePrevious()
    {
        // The test runs as the Expression is typed, so the output always answers the Expression on screen.
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression, "\"first answer\""));
        Assert.That(cut.Markup, Does.Contain("first answer"));

        cut.Render(p => p.Add(c => c.Expression, "\"second answer\""));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("first answer"),
                "the previous output belongs to the previous Expression and must not survive it");
            Assert.That(cut.Markup, Does.Contain("second answer"));
        }
    }

    [Test]
    public void ExpressionTester_ExpressionReturningSeveralValues_ShowsEachValueRatherThanTheTypeName()
    {
        // Several values used to be shown as the list's type name (#2179), which says nothing about what flows.
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression, "Split(\"SOFT101|SEC101\", \"|\")"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("System.String[]"));
            Assert.That(cut.FindAll(".jim-expression-output").Select(e => e.TextContent.Trim()),
                Is.EqualTo(new[] { "SOFT101", "SEC101" }), "one value per line");
        }
    }

    [Test]
    public void ExpressionTester_ExpressionReturningADate_ShowsJimsDateTextForm()
    {
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression, "ParseDate(\"2026-03-15 10:30\", \"yyyy-MM-dd HH:mm\")"));

        Assert.That(cut.FindAll(".jim-expression-output").Select(e => e.TextContent.Trim()),
            Is.EqualTo(new[] { "2026-03-15 10:30:00Z" }), "the tester shows a date as JIM writes it to a Text attribute");
    }

    [Test]
    public void ExpressionTester_ExpressionThatCannotBeParsed_ShowsTheReason()
    {
        var cut = Render<ExpressionTester>(p => p.Add(c => c.Expression, "cs[\"unclosed\""));

        Assert.That(cut.HasComponent<MudAlert>(), Is.True,
            "a malformed Expression must say so here rather than at the next Synchronisation");
    }

    #region Testing the Attribute Flow being edited (#2055)

    private sealed record Flow(SyncRule Rule, SyncRuleMapping Mapping, SyncRuleMappingSource Source);

    private static Flow ImportFlow(string expression, AttributeDataType targetType,
        MissingInputBehaviour missingInputBehaviour = MissingInputBehaviour.EvaluateAnyway)
    {
        var target = new MetaverseAttribute { Id = 21, Name = "Target", Type = targetType };
        var rule = new SyncRule
        {
            Direction = SyncRuleDirection.Import,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = 1, Name = "person",
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "hireDate", Type = AttributeDataType.DateTime },
                    new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "givenName", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 14, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued }
                ]
            },
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = [target] }
        };
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = target };
        var source = new SyncRuleMappingSource { Expression = expression, MissingInputBehaviour = missingInputBehaviour };
        mapping.Sources.Add(source);
        rule.ConnectedSystemId = 3;
        return new Flow(rule, mapping, source);
    }

    private IRenderedComponent<ExpressionTester> RenderFlow(Flow flow) => Render<ExpressionTester>(p => p
        .Add(c => c.Expression, flow.Source.Expression)
        .Add(c => c.SyncRule, flow.Rule)
        .Add(c => c.Mapping, flow.Mapping)
        .Add(c => c.Source, flow.Source));

    private static async Task EnterAsync(IRenderedComponent<ExpressionTester> cut, string label, string value) =>
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>()
            .Single(f => f.Instance.Label == label).Instance.ValueChanged.InvokeAsync(value));

    [Test]
    public async Task ExpressionTester_AttributeFlowRunAsOf_TodayReadsThatInstantAsync()
    {
        var cut = RenderFlow(ImportFlow("FormatDate(Today(), \"yyyy-MM-dd\")", AttributeDataType.Text));

        await cut.InvokeAsync(() => cut.FindComponent<RunAsOfPicker>().Instance.ValueChanged
            .InvokeAsync(new DateTime(2031, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.That(cut.FindAll(".jim-expression-output").Select(e => e.TextContent.Trim()), Does.Contain("2031-06-01"));
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowWithDateInputCast_ShowsTheValueAsASynchronisationWouldAsync()
    {
        // Scenario 14: without the Attribute Flow the sample is text and the cast fails.
        var cut = RenderFlow(ImportFlow("FormatDate((DateTime)cs[\"hireDate\"], \"yyyy-MM-dd\")", AttributeDataType.Text));

        await EnterAsync(cut, "cs[\"hireDate\"]", "2026-03-02");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudAlert>(), Is.Empty, "the cast succeeds on a typed date");
            Assert.That(cut.FindAll(".jim-expression-output").Select(e => e.TextContent.Trim()), Does.Contain("2026-03-02"));
        }
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowResultTheTargetCannotHold_ShowsAnErrorAsync()
    {
        // The false pass: the Expression evaluates, but a Number attribute cannot hold its result.
        var cut = RenderFlow(ImportFlow("cs[\"givenName\"]", AttributeDataType.Number));

        await EnterAsync(cut, "cs[\"givenName\"]", "Priya");

        Assert.That(cut.FindComponents<MudAlert>().Select(a => a.Instance.Severity), Does.Contain(Severity.Error));
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowInputThatIsNotItsType_ShowsTheInputErrorAsync()
    {
        var cut = RenderFlow(ImportFlow("cs[\"hireDate\"]", AttributeDataType.DateTime));

        await EnterAsync(cut, "cs[\"hireDate\"]", "next Tuesday");

        Assert.That(cut.FindComponents<MudAlert>().Single().Instance.Severity, Is.EqualTo(Severity.Error));
    }

    [Test]
    public void ExpressionTester_AttributeFlowWithNoTargetYet_EvaluatesTheExpressionAlone()
    {
        var flow = ImportFlow("\"x\"", AttributeDataType.Text);
        flow.Mapping.TargetMetaverseAttribute = null;
        var cut = RenderFlow(flow);

        Assert.That(cut.FindAll(".jim-expression-output").Select(e => e.TextContent.Trim()), Is.EqualTo(new[] { "x" }));
    }

    [Test]
    public void ExpressionTester_AttributeFlowNothingTriedYet_ShowsNoResult()
    {
        // Until a value is given for an input, a result would only report every input missing, which answers nothing
        // the administrator asked.
        var cut = RenderFlow(ImportFlow("cs[\"givenName\"]", AttributeDataType.Text, MissingInputBehaviour.FailMapping));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudAlert>(), Is.Empty);
            Assert.That(cut.FindAll(".jim-expression-output"), Is.Empty);
        }
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowValueEntered_ShowsTheResultWithoutRunningAsync()
    {
        var cut = RenderFlow(ImportFlow("Upper(cs[\"givenName\"])", AttributeDataType.Text));

        await EnterAsync(cut, "cs[\"givenName\"]", "Priya");

        Assert.That(cut.FindAll(".jim-expression-output").Select(e => e.TextContent.Trim()), Does.Contain("PRIYA"),
            "the test runs as the values are entered");
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowMissingInputBehaviourChanged_ResultFollowsItAsync()
    {
        // The result used to stay as it was run, so a Missing Input Behaviour chosen afterwards was never reflected.
        var flow = ImportFlow("cs[\"givenName\"]", AttributeDataType.Text, MissingInputBehaviour.EvaluateAnyway);
        var cut = RenderFlow(flow);
        var noValue = cut.FindComponents<MudCheckBox<bool>>().Single();
        await cut.InvokeAsync(() => noValue.Instance.ValueChanged.InvokeAsync(true));
        Assert.That(cut.FindComponents<MudAlert>(), Is.Empty, "evaluated anyway, a missing input is no error");

        flow.Source.MissingInputBehaviour = MissingInputBehaviour.FailMapping;
        cut.Render(p => p.Add(c => c.Source, flow.Source));

        Assert.That(cut.FindComponents<MudAlert>().Select(a => a.Instance.Severity), Does.Contain(Severity.Error),
            "Fail this mapping now reports the missing input as an error");
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowInputClearedAfterTyping_IsEmptyTextNotNoValueAsync()
    {
        // Empty text reaches the Expression as empty text; "No value" (the tick box) reaches it as null.
        var cut = RenderFlow(ImportFlow("IIF(cs[\"givenName\"] == null, \"null\", \"empty\")", AttributeDataType.Text));

        await EnterAsync(cut, "cs[\"givenName\"]", "Priya");
        await EnterAsync(cut, "cs[\"givenName\"]", "");

        Assert.That(cut.FindAll(".jim-expression-output").Select(e => e.TextContent.Trim()), Does.Contain("empty"));
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowNoValueTicked_TestsTheInputAsAbsentAsync()
    {
        var cut = RenderFlow(ImportFlow("cs[\"givenName\"]", AttributeDataType.Text, MissingInputBehaviour.FailMapping));
        await EnterAsync(cut, "cs[\"givenName\"]", "Priya");

        var noValue = cut.FindComponents<MudCheckBox<bool>>().Single();
        await cut.InvokeAsync(() => noValue.Instance.ValueChanged.InvokeAsync(true));

        Assert.That(cut.FindComponents<MudAlert>().Select(a => a.Instance.Severity), Does.Contain(Severity.Error));
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowMultiValuedInput_TakesSeveralValuesAsync()
    {
        var cut = RenderFlow(ImportFlow("Join(cs[\"mail\"], \";\")", AttributeDataType.Text));

        foreach (var address in new[] { "a@example.org", "b@example.org" })
        {
            await EnterAsync(cut, "cs[\"mail\"]", address);
            cut.FindAll("button").Single(b => b.TextContent.Contains("Add value")).Click();
        }

        Assert.That(cut.FindComponents<MudChip<string>>().Select(c => c.Instance.Text ?? c.Markup), Has.Some.Contains("b@example.org"),
            "each value added is held as a value of the multi-valued input");
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowLoadedFromAnObject_TestsItsValuesAsync()
    {
        var flow = ImportFlow("FormatDate((DateTime)cs[\"hireDate\"], \"yyyy-MM-dd\")", AttributeDataType.Text);
        var csoId = Guid.NewGuid();
        var hireDate = flow.Rule.ConnectedSystemObjectType.Attributes.Single(a => a.Name == "hireDate");
        var cso = new ConnectedSystemObject { Id = csoId, ConnectedSystemId = 3, TypeId = 1 };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = hireDate, AttributeId = hireDate.Id, DateTimeValue = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc) });
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectHeadersAsync(3, 1, 8, "Priya", null, true, null, It.IsAny<IEnumerable<int>?>(), null, null))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectHeader> { Results = [new ConnectedSystemObjectHeader { Id = csoId, DisplayName = "Priya Shah", ConnectedSystemId = 3, TypeId = 1 }] });
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectAsync(3, csoId)).ReturnsAsync(cso);
        var cut = RenderFlow(flow);

        await cut.InvokeAsync(() => cut.FindComponent<SegmentedToggle<bool>>().Instance.ValueChanged.InvokeAsync(true));
        await cut.InvokeAsync(() => cut.FindComponent<SearchField>().Instance.ValueChanged.InvokeAsync("Priya"));
        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == "Load values from Priya Shah").Click();

        Assert.That(cut.FindAll(".jim-expression-output").Select(e => e.TextContent.Trim()), Does.Contain("2026-03-02"));
    }

#endregion

    #region A Generated Value (#2055 Phase 2d): the base expression, stopping before generation

    private static Flow GeneratedFlow(string baseExpression)
    {
        var flow = ImportFlow(baseExpression, AttributeDataType.Text, MissingInputBehaviour.ContributeNoValue);
        flow.Mapping.Generation = new SyncRuleMappingGeneration();
        flow.Mapping.CaseNormalisation = InboundCaseNormalisation.Lower;
        return flow;
    }

    [Test]
    public async Task ExpressionTester_GeneratedValue_ShowsTheBaseGenerationWouldStartFromAsync()
    {
        var cut = RenderFlow(GeneratedFlow("cs[\"givenName\"]"));

        await EnterAsync(cut, "cs[\"givenName\"]", "Priya");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudAlert>(), Is.Empty, "a base is not an input error");
            Assert.That(cut.FindAll("[data-testid=generated-base]").Select(e => e.TextContent.Trim()), Is.EqualTo(new[] { "priya" }));
        }
    }

    [Test]
    public async Task ExpressionTester_GeneratedValueSeveralValues_ShowsTheErrorAsync()
    {
        var cut = RenderFlow(GeneratedFlow("cs[\"mail\"]"));
        foreach (var address in new[] { "a@corp.local", "b@corp.local" })
        {
            await EnterAsync(cut, "cs[\"mail\"]", address);
            cut.FindAll("button").Single(b => b.TextContent.Contains("Add value")).Click();
        }

        Assert.That(cut.FindComponents<MudAlert>().Select(a => a.Instance.Severity), Does.Contain(Severity.Error));
    }

    [Test]
    public async Task ExpressionTester_AttributeFlowInputEdited_ReportsTheSampleValuesAsync()
    {
        // The Generated Value editor's candidate preview evaluates against these same samples.
        IReadOnlyDictionary<string, string?>? reported = null;
        var flow = GeneratedFlow("cs[\"givenName\"]");
        var cut = Render<ExpressionTester>(p => p
            .Add(c => c.Expression, flow.Source.Expression)
            .Add(c => c.SyncRule, flow.Rule)
            .Add(c => c.Mapping, flow.Mapping)
            .Add(c => c.Source, flow.Source)
            .Add(c => c.SampleValuesChanged, values => reported = values));

        await EnterAsync(cut, "cs[\"givenName\"]", "Priya");

        Assert.That(reported?["cs[\"givenName\"]"], Is.EqualTo("Priya"));
    }

    [Test]
    public async Task ExpressionTester_GeneratedValue_ReportsTheBaseItTriedAsync()
    {
        // The Generated Value's candidates are worked out from this base, typed and value-processed as a
        // Synchronisation would, rather than from the raw sample text.
        var reported = new List<GeneratedBaseEvaluation?>();
        var flow = GeneratedFlow("cs[\"givenName\"]");
        var cut = Render<ExpressionTester>(p => p
            .Add(c => c.Expression, flow.Source.Expression)
            .Add(c => c.SyncRule, flow.Rule)
            .Add(c => c.Mapping, flow.Mapping)
            .Add(c => c.Source, flow.Source)
            .Add(c => c.TriedBaseChanged, tried => reported.Add(tried)));

        await EnterAsync(cut, "cs[\"givenName\"]", "Priya");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reported.LastOrDefault()?.Outcome, Is.EqualTo(GeneratedBaseOutcome.Base));
            Assert.That(reported.LastOrDefault()?.BaseValue, Is.EqualTo("priya"), "lower-cased by value processing");
        }
    }

    #endregion
}
