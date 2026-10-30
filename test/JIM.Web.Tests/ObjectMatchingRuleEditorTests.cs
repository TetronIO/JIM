// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Object Matching Rule editor (#2052), shared by the Connected System's Matching tab (simple mode) and a
/// Synchronisation Rule's (advanced mode): which inputs it offers for which source, whether the rule as composed can
/// be added, why not, and what Try it says the rule would compare.
/// </summary>
[TestFixture]
public class ObjectMatchingRuleEditorTests : JimComponentTestContext
{
    private readonly Mock<ISyncRepository> _syncRepository = new();
    private ConnectedSystemObjectType _objectType = null!;
    private MetaverseAttribute _employeeId = null!;
    private MetaverseAttribute _leaver = null!;

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new Mock<IRepository>().Object, _syncRepository.Object));
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    [SetUp]
    public void SetUp()
    {
        _objectType = new ConnectedSystemObjectType
        {
            Id = 7,
            Name = "user",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = 102, Name = "mail", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = 103, Name = "pager", Type = AttributeDataType.Text, Selected = false }
            ]
        };
        _employeeId = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text };
        _leaver = new MetaverseAttribute { Id = 202, Name = "Leaver", Type = AttributeDataType.Boolean };
    }

    [Test]
    public void Editor_AttributeSource_OffersTheAttributePickerAndNoExpressionEditor()
    {
        var cut = RenderEditor(new ObjectMatchingRuleDraft());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudSelect<ConnectedSystemObjectTypeAttribute>>(), Has.Count.EqualTo(1));
            Assert.That(cut.HasComponent<ExpressionEditor>(), Is.False);
        }
    }

    [Test]
    public void Editor_ExpressionSource_OffersTheExpressionEditorAndOnlyTheImportedConnectedSystemAttributesToInsert()
    {
        var cut = RenderEditor(new ObjectMatchingRuleDraft { SourceKind = ObjectMatchingRuleSourceKind.Expression });

        var menu = cut.FindComponent<InsertAttributeMenu>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<ExpressionEditor>(), Is.True);
            Assert.That(cut.FindComponents<MudSelect<ConnectedSystemObjectTypeAttribute>>(), Is.Empty);
            // A matching expression reads the Connected System Object only, so there is no Metaverse group to offer.
            Assert.That(menu.MetaverseAttributes, Is.Null.Or.Empty);
            Assert.That(menu.ConnectedSystemAttributes!.Select(a => a.Name), Is.EquivalentTo(new[] { "employeeId", "mail" }));
        }
    }

    [Test]
    public void Editor_SwitchingTheSource_ChangesWhatTheDraftHolds()
    {
        var draft = new ObjectMatchingRuleDraft();
        var cut = RenderEditor(draft);

        var toggle = cut.FindComponent<SegmentedToggle<ObjectMatchingRuleSourceKind>>();
        cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(ObjectMatchingRuleSourceKind.Expression));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(draft.SourceKind, Is.EqualTo(ObjectMatchingRuleSourceKind.Expression));
            Assert.That(cut.HasComponent<ExpressionEditor>(), Is.True);
        }
    }

    [Test]
    public void Editor_WithMetaverseObjectTypes_AsksForTheTypeToSearch()
    {
        var cut = RenderEditor(new ObjectMatchingRuleDraft(), metaverseObjectTypes: [new MetaverseObjectType { Id = 1, Name = "Person" }]);

        Assert.That(cut.FindComponents<MudSelect<MetaverseObjectType>>(), Has.Count.EqualTo(1));
    }

    [Test]
    public void Editor_WithoutMetaverseObjectTypes_LeavesTheTypeToTheSynchronisationRule()
    {
        var cut = RenderEditor(new ObjectMatchingRuleDraft());

        Assert.That(cut.FindComponents<MudSelect<MetaverseObjectType>>(), Is.Empty);
    }

    [Test]
    public void Editor_UnmatchableMetaverseAttribute_IsListedButCannotBeChosen()
    {
        var cut = RenderEditor(new ObjectMatchingRuleDraft());

        var items = cut.FindComponent<MudSelect<MetaverseAttribute>>().FindComponents<MudSelectItem<MetaverseAttribute>>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(items.Single(i => i.Instance.Value == _leaver).Instance.Disabled, Is.True);
            Assert.That(items.Single(i => i.Instance.Value == _employeeId).Instance.Disabled, Is.False);
        }
    }

    [Test]
    public void Editor_ExpressionTheSaveWouldRefuse_SaysWhyAndCannotBeAdded()
    {
        var draft = new ObjectMatchingRuleDraft { SourceKind = ObjectMatchingRuleSourceKind.Expression, TargetAttribute = _employeeId };
        var cut = RenderEditor(draft);

        TypeExpression(cut, "Trim(cs[\"employeeId\"]) + Trim(mv[\"Department\"])");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(draft.CanAdd(requireMetaverseObjectType: false), Is.False);
            Assert.That(cut.Find("[data-testid='jim-matching-rule-problem']").TextContent, Does.Contain("mv[\"Department\"]"));
        }
    }

    [Test]
    public void Editor_WorkableExpression_CanBeAdded()
    {
        var draft = new ObjectMatchingRuleDraft { SourceKind = ObjectMatchingRuleSourceKind.Expression, TargetAttribute = _employeeId };
        var cut = RenderEditor(draft);

        TypeExpression(cut, "\"E\" + Trim(cs[\"employeeId\"])");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(draft.CanAdd(requireMetaverseObjectType: false), Is.True);
            Assert.That(cut.FindAll("[data-testid='jim-matching-rule-problem']"), Is.Empty);
        }
    }

    [Test]
    public void Editor_SimpleModeDraftWithNoMetaverseObjectType_CannotBeAdded()
    {
        var draft = new ObjectMatchingRuleDraft { SourceAttribute = _objectType.Attributes[0], TargetAttribute = _employeeId };
        RenderEditor(draft, metaverseObjectTypes: [new MetaverseObjectType { Id = 1, Name = "Person" }]);

        Assert.That(draft.CanAdd(requireMetaverseObjectType: true), Is.False);
    }

    [Test]
    public async Task TryIt_ExpressionWithASample_ShowsTheValueItWouldMatchOnAndItsTypeAsync()
    {
        var draft = new ObjectMatchingRuleDraft { SourceKind = ObjectMatchingRuleSourceKind.Expression, TargetAttribute = _employeeId };
        var cut = RenderEditor(draft);
        TypeExpression(cut, "\"E\" + Trim(cs[\"employeeId\"])");

        var input = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "cs[\"employeeId\"]");
        await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync(" 1007 "));
        await cut.Find("[data-testid='jim-matching-rule-try-it'] button").ClickAsync();

        var output = cut.WaitForElement("[data-testid='jim-matching-rule-try-it-result']");
        Assert.That(output.TextContent, Does.Contain("E1007").And.Contain("Text"));
    }

    [Test]
    public async Task TryIt_NoValue_SaysTheNextRuleWouldBeTriedAsync()
    {
        var draft = new ObjectMatchingRuleDraft { SourceKind = ObjectMatchingRuleSourceKind.Expression, TargetAttribute = _employeeId };
        var cut = RenderEditor(draft);
        TypeExpression(cut, "Trim(cs[\"employeeId\"])");

        await cut.Find("[data-testid='jim-matching-rule-try-it'] button").ClickAsync();

        var output = cut.WaitForElement("[data-testid='jim-matching-rule-try-it-result']");
        Assert.That(output.TextContent, Does.Contain("next rule"));
    }

    [Test]
    public async Task TryIt_SampleThatIsNotItsAttributesType_ShowsTheInputErrorInsteadOfAResultAsync()
    {
        _objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 104, Name = "hireDate", Type = AttributeDataType.DateTime, Selected = true });
        var draft = new ObjectMatchingRuleDraft { SourceKind = ObjectMatchingRuleSourceKind.Expression, TargetAttribute = _employeeId };
        var cut = RenderEditor(draft);
        TypeExpression(cut, "FormatDate((DateTime)cs[\"hireDate\"], \"yyyy\")");

        var input = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "cs[\"hireDate\"]");
        await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync("soon"));
        await cut.Find("[data-testid='jim-matching-rule-try-it'] button").ClickAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid='jim-matching-rule-try-it-result']"), Is.Empty);
            Assert.That(cut.FindComponent<MudAlert>().Markup, Does.Contain("soon"));
        }
    }

    [Test]
    public void TryIt_NoMetaverseObjectTypeKnown_OffersNoLookup()
    {
        var cut = RenderEditor(new ObjectMatchingRuleDraft { SourceAttribute = _objectType.Attributes[0], TargetAttribute = _employeeId });

        Assert.That(cut.Markup, Does.Not.Contain("Also find the Metaverse Object"));
    }

    [Test]
    public async Task TryIt_LookUp_ShowsTheMetaverseObjectASynchronisationWouldJoinAsync()
    {
        var person = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" };
        var identity = new MetaverseObject { Id = Guid.NewGuid(), Type = person };
        identity.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = identity, Attribute = _employeeId, AttributeId = _employeeId.Id, StringValue = "E1007" });
        _syncRepository.Setup(r => r.FindMetaverseObjectIdsByMatchValuesAsync(person.Id, _employeeId.Id, AttributeDataType.Text,
                It.Is<IReadOnlyList<object>>(values => values.Single().Equals("E1007")), It.IsAny<bool>(), It.IsAny<int>()))
            .ReturnsAsync([identity.Id]);
        _syncRepository.Setup(r => r.GetMetaverseObjectForJoinMatchAsync(identity.Id)).ReturnsAsync(identity);
        var cut = RenderEditor(new ObjectMatchingRuleDraft { SourceAttribute = _objectType.Attributes[0], TargetAttribute = _employeeId },
            lookupType: person);

        var input = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "cs[\"employeeId\"]");
        await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync("E1007"));
        await cut.Find("[data-testid='jim-matching-rule-try-it'] button").ClickAsync();

        var lookup = cut.WaitForElement("[data-testid='jim-matching-rule-try-it-lookup']");
        Assert.That(lookup.TextContent, Does.Contain("Would join").And.Contain("would join this object"));
    }

    [Test]
    public void Draft_ExpressionSource_BuildsARuleAndAProposalCarryingOnlyTheExpression()
    {
        var draft = new ObjectMatchingRuleDraft
        {
            SourceKind = ObjectMatchingRuleSourceKind.Expression,
            SourceAttribute = _objectType.Attributes[0],
            Expression = "Lower(cs[\"mail\"])",
            TargetAttribute = _employeeId,
            CaseSensitive = true
        };

        var rule = draft.ToRule(order: 3);
        var proposal = draft.ToSourceProposal();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rule.Order, Is.EqualTo(3));
            Assert.That(rule.CaseSensitive, Is.True);
            Assert.That(rule.TargetMetaverseAttributeId, Is.EqualTo(_employeeId.Id));
            Assert.That(rule.Sources.Single().Expression, Is.EqualTo("Lower(cs[\"mail\"])"));
            Assert.That(rule.Sources.Single().ConnectedSystemAttributeId, Is.Null,
                "a source left over from the other kind must not ride along");
            Assert.That(proposal.Expression, Is.EqualTo("Lower(cs[\"mail\"])"));
            Assert.That(proposal.ConnectedSystemAttributeId, Is.Null);
        }
    }

    private IRenderedComponent<ObjectMatchingRuleEditor> RenderEditor(ObjectMatchingRuleDraft draft,
        IReadOnlyList<MetaverseObjectType>? metaverseObjectTypes = null, MetaverseObjectType? lookupType = null) =>
        Render<ObjectMatchingRuleEditor>(p => p
            .Add(c => c.Draft, draft)
            .Add(c => c.LookupMetaverseObjectType, lookupType)
            .Add(c => c.ObjectType, _objectType)
            .Add(c => c.MetaverseAttributes, new[] { _employeeId, _leaver })
            .Add(c => c.MetaverseObjectTypes, metaverseObjectTypes)
            .Add(c => c.ConnectedSystemName, "Directory"));

    private static void TypeExpression(IRenderedComponent<ObjectMatchingRuleEditor> cut, string expression)
    {
        var editor = cut.FindComponent<ExpressionEditor>();
        cut.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync(expression));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository, ISyncRepository syncRepository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository, syncRepository: syncRepository);
    }
}
