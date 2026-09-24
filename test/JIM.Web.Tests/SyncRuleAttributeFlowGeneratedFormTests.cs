// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using Bunit;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.TestSupport;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The "JIM generates it" Source Type on the Attribute Flow Add/Edit dialog (Unique Value Generation, #242,
/// Phase 3): selecting and leaving it, the uniqueness token's sub-controls per kind, and the Number-target
/// restrictions. These drive <see cref="SyncRuleAttributeFlowTab"/>'s dialog directly (the pattern established by
/// <see cref="SyncRuleAttributeFlowTabTests"/>) rather than <see cref="GeneratedValueOptionsEditor"/> in
/// isolation, because the target picker and the Generation lifecycle (create on select, clear on leave) are the
/// parent's responsibility.
/// </summary>
[TestFixture]
public class SyncRuleAttributeFlowGeneratedFormTests : JimComponentTestContext
{
    private JimApplication _jim = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repo = new Mock<IRepository>();
        // Unique Value Generation is gated behind its feature flag (#242, Phase 3.5); this file exercises the
        // generated form itself, so run with it enabled (test/CLAUDE.md > "Tests run with flags on"). The gate's
        // own visibility behaviour is covered by SyncRuleAttributeFlowTabFeatureFlagTests.
        repo.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        _jim = new JimApplication(repo.Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public void SelectingGenerated_ShowsAssignTheValueToTargetPicker()
    {
        var (provider, _, _) = OpenAddDialogWithGeneratedSourceType();

        var targetPicker = provider.FindComponents<MudSelect<MetaverseAttribute>>()
            .SingleOrDefault(s => s.Instance.Label == "Assign the value to");

        Assert.That(targetPicker, Is.Not.Null, "the target picker must come first, per the approved mockup's field order");
    }

    [Test]
    public void SelectingGenerated_CreatesGenerationWithModelDefaults()
    {
        var (_, tab, mapping) = OpenAddDialogWithGeneratedSourceType();
        _ = tab;

        Assert.That(mapping().Generation, Is.Not.Null);
        Assert.That(mapping().Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.OnlyIfTaken),
            "the model's own default, which the form must not override");
    }

    [Test]
    public void SwitchingAwayFromGenerated_RemovesGeneration()
    {
        var (provider, tab, mapping) = OpenAddDialogWithGeneratedSourceType();
        Assert.That(mapping().Generation, Is.Not.Null, "precondition");

        var sourceTypePicker = provider.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Source Type");
        provider.InvokeAsync(() => sourceTypePicker.Instance.ValueChanged.InvokeAsync("Expression")).GetAwaiter().GetResult();
        tab.Render();

        Assert.That(mapping().Generation, Is.Null, "switching away must remove the generated mapping's settings row");
    }

    [Test]
    public async Task SelectingTargetThenTokenKind_SequenceShowsStartAndIncrementFields()
    {
        var (provider, tab, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var tokenRadios = provider.FindComponents<MudRadioGroup<GeneratedValueTokenKind>>().Single();
        await provider.InvokeAsync(() => tokenRadios.Instance.ValueChanged.InvokeAsync(GeneratedValueTokenKind.Sequence));
        tab.Render();

        var numericLabels = provider.FindComponents<MudNumericField<long>>().Select(f => f.Instance.Label)
            .Concat(provider.FindComponents<MudNumericField<int>>().Select(f => f.Instance.Label))
            .ToList();

        Assert.That(numericLabels, Does.Contain("Start at"));
        Assert.That(numericLabels, Does.Contain("Increment"));
    }

    [Test]
    public async Task SelectingTargetThenTokenKind_RandomShowsFormatPicker()
    {
        var (provider, tab, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var tokenRadios = provider.FindComponents<MudRadioGroup<GeneratedValueTokenKind>>().Single();
        await provider.InvokeAsync(() => tokenRadios.Instance.ValueChanged.InvokeAsync(GeneratedValueTokenKind.Random));
        tab.Render();

        var formatPicker = provider.FindComponents<MudSelect<GeneratedValueRandomFormat>>().SingleOrDefault(s => s.Instance.Label == "Format");
        Assert.That(formatPicker, Is.Not.Null);
    }

    // MudBlazor's MUD0012 analyzer forbids external reads of a two-way-bound parameter (Value/Disabled) via
    // ParameterState; its suggested GetState(x => x.Value) is an internal API for MudBlazor's own components,
    // not exposed to consumers. Reading these directly is the same pattern this suite's sibling
    // (SyncRuleAttributeFlowTabTests) already uses for MudSelect (ToStringFunc, Label), and is JIM's own test
    // asserting on a component's own parameters, per test/CLAUDE.md's bUnit guidance; suppressed narrowly here.
#pragma warning disable MUD0012

    [Test]
    public async Task SelectingNumberTargetWhileOnlyIfTakenSelected_SwitchesToSequence()
    {
        // Only-if-taken is the model default and is picked before any target is chosen; switching to a Number
        // target must move off it automatically (SyncRuleMappingGenerationValidator rule 4 rejects it), matching
        // the approved mockup's Number-target frame, which shows a sequence number pre-selected.
        var (_, _, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Number);

        Assert.That(mapping().Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.Sequence));
    }

    [Test]
    public async Task NumberTarget_OnlyIfTakenRadioIsDisabled()
    {
        var (provider, _, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Number);

        var onlyIfTaken = provider.FindComponents<MudRadio<GeneratedValueTokenKind>>()
            .Single(r => Equals(r.Instance.Value, GeneratedValueTokenKind.OnlyIfTaken));

        Assert.That(onlyIfTaken.Instance.Disabled, Is.True,
            "a Number target cannot use \"Only if taken\" (SyncRuleMappingGenerationValidator rule 4)");
    }

    [Test]
    public async Task NumberTarget_BaseExpressionFieldIsDisabled()
    {
        var (provider, _, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Number);

        var baseExpression = provider.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "Base expression");

        Assert.That(baseExpression.Instance.Disabled, Is.True,
            "a Number target cannot have a base expression: a prefix or letters would make the value text");
    }

    [Test]
    public async Task NumberTarget_RandomFormatMenu_RestrictsGuidAndHexToDigitsOnly()
    {
        var (provider, tab, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Number);

        var tokenRadios = provider.FindComponents<MudRadioGroup<GeneratedValueTokenKind>>().Single();
        await provider.InvokeAsync(() => tokenRadios.Instance.ValueChanged.InvokeAsync(GeneratedValueTokenKind.Random));
        tab.Render();

        var formatSelect = provider.FindComponents<MudSelect<GeneratedValueRandomFormat>>().Single(s => s.Instance.Label == "Format");
        var items = formatSelect.FindComponents<MudSelectItem<GeneratedValueRandomFormat>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(items.Single(i => Equals(i.Instance.Value, GeneratedValueRandomFormat.Guid)).Instance.Disabled, Is.True);
            Assert.That(items.Single(i => Equals(i.Instance.Value, GeneratedValueRandomFormat.Hex)).Instance.Disabled, Is.True);
            Assert.That(items.Single(i => Equals(i.Instance.Value, GeneratedValueRandomFormat.Digits)).Instance.Disabled, Is.False);
        }
    }

#pragma warning restore MUD0012

    [Test]
    public async Task TypingABaseExpression_ShowsSeparatorAndMissingInputBehaviour()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var baseExpression = provider.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "Base expression");
        await provider.InvokeAsync(() => baseExpression.Instance.ValueChanged.InvokeAsync("Lower(cs[\"firstName\"])"));
        tab.Render();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindComponents<MudSelect<string>>().Any(s => s.Instance.Label == "Separator"), Is.True,
                "a separator is only offered alongside a base expression on a Text target");
            Assert.That(provider.FindComponents<MudRadioGroup<MissingInputBehaviour>>().Any(), Is.True,
                "Missing Input Behaviour is only offered once a base expression is present");
        }

        Assert.That(mapping().Sources.Any(s => s.Expression == "Lower(cs[\"firstName\"])"), Is.True,
            "a non-empty base expression must be added to Sources, or SyncRuleMappingGenerationValidator misreads it");
    }

    [Test]
    public async Task NoBaseExpression_HidesSeparatorAndMissingInputBehaviour()
    {
        var (provider, _, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindComponents<MudSelect<string>>().Any(s => s.Instance.Label == "Separator"), Is.False);
            Assert.That(provider.FindComponents<MudRadioGroup<MissingInputBehaviour>>().Any(), Is.False);
        }
    }

    [Test]
    public async Task SwitchingToGenerated_DefaultsMissingInputBehaviourToWait()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var baseExpression = provider.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "Base expression");
        await provider.InvokeAsync(() => baseExpression.Instance.ValueChanged.InvokeAsync("Lower(cs[\"firstName\"])"));
        tab.Render();

        var source = mapping().Sources.Single();
        Assert.That(source.MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.ContributeNoValue),
            "\"Wait until every input has a value\" is the generated-value default (plan Phase 3 point 4)");
    }

    // ─── Test scaffolding ───

    private (IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleAttributeFlowTab> Tab, Func<SyncRuleMapping> Mapping)
        OpenAddDialogWithGeneratedSourceType()
    {
        return OpenAddDialogWithGeneratedSourceType(BuildRule());
    }

    private (IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleAttributeFlowTab> Tab, Func<SyncRuleMapping> Mapping)
        OpenAddDialogWithGeneratedSourceType(SyncRule rule)
    {
        var provider = Render<MudDialogProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, rule));

        tab.FindAll("button").First(b => b.TextContent.Contains("Add Attribute Flow")).Click();

        provider.WaitForAssertion(() => Assert.That(provider.HasComponent<MudSelect<string>>(), Is.True));
        var sourceTypePicker = provider.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Source Type");
        provider.InvokeAsync(() => sourceTypePicker.Instance.ValueChanged.InvokeAsync("Generated")).GetAwaiter().GetResult();
        tab.Render();

        return (provider, tab, () => GetMapping(tab));
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleAttributeFlowTab> Tab, Func<SyncRuleMapping> Mapping)>
        OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType targetType)
    {
        var rule = BuildRule();
        var (provider, tab, mapping) = OpenAddDialogWithGeneratedSourceType(rule);

        var targetPicker = provider.FindComponents<MudSelect<MetaverseAttribute>>().Single(s => s.Instance.Label == "Assign the value to");
        var target = rule.MetaverseObjectType!.Attributes.Single(a => a.Type == targetType);
        await provider.InvokeAsync(() => targetPicker.Instance.ValueChanged.InvokeAsync(target));
        tab.Render();

        return (provider, tab, mapping);
    }

    private static SyncRuleMapping GetMapping(IRenderedComponent<SyncRuleAttributeFlowTab> tab) =>
        (SyncRuleMapping)GetPrivateField(tab.Instance, "_attributeFlowMapping")!;

    private static object? GetPrivateField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);

    private static SyncRule BuildRule()
    {
        var textAttribute = new MetaverseAttribute { Id = 1, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var numberAttribute = new MetaverseAttribute { Id = 2, Name = "Employee Number", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued };

        var metaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User" };
        metaverseObjectType.Attributes.Add(textAttribute);
        metaverseObjectType.Attributes.Add(numberAttribute);

        var connectedSystemObjectType = new ConnectedSystemObjectType { Id = 2, Name = "user" };
        connectedSystemObjectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Id = 5, Name = "firstName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
        });

        return new SyncRule
        {
            Id = 4,
            Name = "HR Import",
            Direction = SyncRuleDirection.Import,
            MetaverseObjectType = metaverseObjectType,
            ConnectedSystemObjectType = connectedSystemObjectType
        };
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
