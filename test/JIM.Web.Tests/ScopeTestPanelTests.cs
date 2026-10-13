// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Component tests for the Scope tester panel (#2055): the inputs it offers follow the criteria, an untouched input
/// has no value, values typed or loaded from an object reach the Scope tester, and "Run as of" moves relative dates.
/// The evaluation itself is the Scope tester's, proven in <c>ScopeTesterTests</c>; these pin the wiring.
/// </summary>
[TestFixture]
public class ScopeTestPanelTests : JimComponentTestContext
{
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private ConnectedSystemObjectType _csoType = null!;

    [SetUp]
    public void SetUp()
    {
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));

        _csoType = new ConnectedSystemObjectType
        {
            Id = 1, Name = "person",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "department", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "leaveDate", Type = AttributeDataType.DateTime }
            ]
        };
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }

    /// <summary>An import rule in scope for the Finance department until 30 days after the leave date.</summary>
    private SyncRule ImportRule()
    {
        var rule = new SyncRule
        {
            Id = 7, Name = "HR Import", Direction = SyncRuleDirection.Import, ConnectedSystemId = 3,
            ConnectedSystemObjectType = _csoType, MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" }
        };
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = _csoType.Attributes[0], ComparisonType = SearchComparisonType.Equals, StringValue = "Finance"
        });
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = _csoType.Attributes[1], ComparisonType = SearchComparisonType.GreaterThan,
            ValueMode = DateCriteriaValueMode.Relative, RelativeCount = 30, RelativeUnit = RelativeDateUnit.Days,
            RelativeDirection = RelativeDateDirection.Ago
        });
        rule.ObjectScopingCriteriaGroups.Add(group);
        return rule;
    }

    private IRenderedComponent<ScopeTestPanel> RenderPanel(SyncRule rule) => Render<ScopeTestPanel>(p => p.Add(c => c.SyncRule, rule));

    private static async Task EnterAsync(IRenderedComponent<ScopeTestPanel> cut, string label, string value) =>
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>()
            .Single(f => f.Instance.Label == label).Instance.ValueChanged.InvokeAsync(value));

    private static void ClickRunTest(IRenderedComponent<ScopeTestPanel> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Run test")).Click();

    private static ScopingExplanation Explanation(IRenderedComponent<ScopeTestPanel> cut) =>
        cut.FindComponent<ScopingExplanationView>().Instance.Explanation;

    [Test]
    public void ScopeTestPanel_OffersAnEditorForEachComparedAttribute()
    {
        var cut = RenderPanel(ImportRule());

        Assert.That(cut.FindComponents<TestInputValueEditor>().Select(e => e.Instance.Label),
            Is.EqualTo(new[] { "department", "leaveDate" }));
    }

    [Test]
    public void ScopeTestPanel_UntouchedInputs_AreTestedWithNoValue()
    {
        var cut = RenderPanel(ImportRule());

        ClickRunTest(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Explanation(cut).Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
            Assert.That(Explanation(cut).Groups.Single().Criteria.Select(c => c.Outcome), Is.All.EqualTo(ScopingCriterionOutcome.NoValue));
        }
    }

    [Test]
    public async Task ScopeTestPanel_TypedValues_AreReadAsTheirTypesAndShowTheNextChangeAsync()
    {
        var cut = RenderPanel(ImportRule());
        var leaveDate = DateTime.UtcNow.Date.AddDays(-1);

        await EnterAsync(cut, "department", "Finance");
        await EnterAsync(cut, "leaveDate", leaveDate.ToString("yyyy-MM-dd"));
        ClickRunTest(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Explanation(cut).Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(cut.Find("[data-testid=jim-scope-test-next-change]").TextContent, Does.StartWith("Leaves scope"));
        }
    }

    [Test]
    public async Task ScopeTestPanel_RunAsOf_ResolvesRelativeDatesAgainstThatInstantAsync()
    {
        var cut = RenderPanel(ImportRule());
        await EnterAsync(cut, "department", "Finance");
        await EnterAsync(cut, "leaveDate", "2026-11-01");

        var asOf = new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        await cut.InvokeAsync(() => cut.FindComponent<RunAsOfPicker>().Instance.ValueChanged.InvokeAsync(asOf));
        ClickRunTest(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Explanation(cut).EvaluatedAt, Is.EqualTo(asOf));
            Assert.That(Explanation(cut).Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
        }
    }

    [Test]
    public async Task ScopeTestPanel_InvalidDate_ShowsTheInputErrorAsync()
    {
        var cut = RenderPanel(ImportRule());

        await EnterAsync(cut, "leaveDate", "soon");
        ClickRunTest(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<ScopingExplanationView>(), Is.Empty);
            Assert.That(cut.FindComponent<MudAlert>().Markup, Does.Contain("soon"));
        }
    }

    [Test]
    public async Task ScopeTestPanel_LoadFromAConnectedSystemObject_FillsTheInputsAsync()
    {
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId, TypeId = _csoType.Id, ConnectedSystemId = 3 };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 11, Attribute = _csoType.Attributes[0], StringValue = "Finance" });
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectHeadersAsync(3, 1, 8, "Priya", null, true, null, It.IsAny<IEnumerable<int>?>(), null, null))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectHeader> { Results = [new ConnectedSystemObjectHeader { Id = csoId, DisplayName = "Priya Shah", ConnectedSystemId = 3, TypeId = 1 }] });
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectAsync(3, csoId)).ReturnsAsync(cso);
        var cut = RenderPanel(ImportRule());

        await cut.InvokeAsync(() => cut.FindComponent<SegmentedToggle<bool>>().Instance.ValueChanged.InvokeAsync(true));
        await cut.InvokeAsync(() => cut.FindComponent<SearchField>().Instance.ValueChanged.InvokeAsync("Priya"));
        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == "Load values from Priya Shah").Click();
        ClickRunTest(cut);

        Assert.That(Explanation(cut).Groups.Single().Criteria[0].Outcome, Is.EqualTo(ScopingCriterionOutcome.Met));
    }
}
