// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.TestSupport;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The "Generated Value" Source Type on the Attribute Flow Add/Edit dialog (Unique Value Generation, #242,
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
        repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        // The dialog's live Metaverse-Derived Attribute Flow analysis (#1750) reads the Metaverse
        // Object Type's import rules once a generated import mapping has a target; there are none here.
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRule>());
        // The generated form's "Checked for availability in" panel (#242, release 3): no export rules here.
        connectedSystems.Setup(r => r.GetExportSyncRulesWithAttributeFlowsAsync()).ReturnsAsync(() => []);
        connectedSystems.Setup(r => r.GetConnectedSystemsWithConnectorDefinitionsAsync()).ReturnsAsync(() => []);
        repo.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);
        repo.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);
        // The tab reads the retired values register's counts once the rule has a generated row (#242, Phase 6).
        var syncRepo = new Mock<ISyncRepository>();
        syncRepo.Setup(r => r.GetRetiredGeneratedValueCountsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new List<JIM.Models.Transactional.RetiredGeneratedValueCount>());
        _jim = new JimApplication(repo.Object, syncRepository: syncRepo.Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public async Task SelectingGenerated_ShowsAssignTheValueToTargetPickerAsync()
    {
        var (provider, _, _) = await OpenAddDialogWithGeneratedSourceTypeAsync();

        var targetPicker = provider.FindComponents<MudSelect<MetaverseAttribute>>()
            .SingleOrDefault(s => s.Instance.Label == "Assign the value to");

        Assert.That(targetPicker, Is.Not.Null, "the target picker must come first, per the approved mockup's field order");
    }

    [Test]
    public async Task SelectingGenerated_CreatesGenerationWithModelDefaultsAsync()
    {
        var (_, tab, mapping) = await OpenAddDialogWithGeneratedSourceTypeAsync();
        _ = tab;

        Assert.That(mapping().Generation, Is.Not.Null);
        Assert.That(mapping().Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.OnlyIfTaken),
            "the model's own default, which the form must not override");
    }

    [Test]
    public async Task SwitchingAwayFromGenerated_RemovesGenerationAsync()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedSourceTypeAsync();
        Assert.That(mapping().Generation, Is.Not.Null, "precondition");

        var sourceTypePicker = provider.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Source Type");
        provider.InvokeAsync(() => sourceTypePicker.Instance.ValueChanged.InvokeAsync("Expression")).GetAwaiter().GetResult();
        tab.Render();

        Assert.That(mapping().Generation, Is.Null, "switching away must remove the generated mapping's settings row");
    }

    [Test]
    public async Task SelectingExpression_RendersExpressionEditorBoundToTheMappingSourceAsync()
    {
        var (provider, tab, _) = await OpenAddDialogWithGeneratedSourceTypeAsync();
        var sourceTypePicker = provider.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Source Type");
        provider.InvokeAsync(() => sourceTypePicker.Instance.ValueChanged.InvokeAsync("Expression")).GetAwaiter().GetResult();
        tab.Render();

        var editor = provider.FindComponents<ExpressionEditor>().Single(e => e.Instance.Label == "Expression");
        provider.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync("Upper(cs[\"firstName\"])")).GetAwaiter().GetResult();

        var source = (SyncRuleMappingSource)GetPrivateField(tab.Instance, "_attributeFlowMappingSource")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(editor.Instance.Required, Is.True, "an Expression source cannot be saved without an expression");
            Assert.That(source.Expression, Is.EqualTo("Upper(cs[\"firstName\"])"));
        }
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

        var baseExpression = provider.FindComponents<ExpressionEditor>().Single(f => f.Instance.Label == "Base expression");

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
    public async Task TryingAValue_KeepsItAndWorksTheCandidatesOutFromItAsync()
    {
        // The tester renders into the dialog's Try it pane (#2471); a value tried there must survive the re-render the
        // form does on every change, and the result must follow it.
        var (provider, tab, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);
        var baseExpression = provider.FindComponents<ExpressionEditor>().Single(f => f.Instance.Label == "Base expression");
        await provider.InvokeAsync(() => baseExpression.Instance.ValueChanged.InvokeAsync("Lower(cs[\"firstName\"])"));
        tab.Render();

        var field = provider.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "cs[\"firstName\"]");
        await provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("James"));
        tab.Render();

        provider.WaitForState(() => provider.Markup.Contains("james1"), TimeSpan.FromSeconds(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindComponents<TestInputValueEditor>().Single().Instance.State.Values, Is.EqualTo(new[] { "James" }),
                "the value tried is kept");
            Assert.That(provider.Markup, Does.Contain("james1"), "the candidates are worked out from the base tried");
        }
    }

    [Test]
    public async Task TypingABaseExpression_ShowsSeparatorAndMissingInputBehaviour()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var baseExpression = provider.FindComponents<ExpressionEditor>().Single(f => f.Instance.Label == "Base expression");
        await provider.InvokeAsync(() => baseExpression.Instance.ValueChanged.InvokeAsync("Lower(cs[\"firstName\"])"));
        tab.Render();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FindComponents<MudSelect<string>>().Any(s => s.Instance.Label == "Separator"), Is.True,
                "a separator is only offered alongside a base expression on a Text target");
            Assert.That(provider.FindComponents<SettingsSection>().Any(section => section.Instance.Title == "If an input has no value"), Is.True,
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
            Assert.That(provider.FindComponents<SettingsSection>().Any(section => section.Instance.Title == "If an input has no value"), Is.False);
        }
    }

    [Test]
    public async Task SwitchingToGenerated_DefaultsMissingInputBehaviourToWait()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var baseExpression = provider.FindComponents<ExpressionEditor>().Single(f => f.Instance.Label == "Base expression");
        await provider.InvokeAsync(() => baseExpression.Instance.ValueChanged.InvokeAsync("Lower(cs[\"firstName\"])"));
        tab.Render();

        var source = mapping().Sources.Single();
        Assert.That(source.MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.ContributeNoValue),
            "\"Wait until every input has a value\" is the generated-value default (plan Phase 3 point 4)");
    }

    // ─── Never reuse a value (#242, Phase 6, mockup section D) ───

    private const string NeverReuseLabel = "Never reuse a value";

    [Test]
    public async Task OnlyIfTakenToken_OffersTheNeverReuseSwitch_OnByDefault()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);
        await TypeBaseExpressionAsync(provider, tab, "Lower(cs[\"firstName\"])");

        var neverReuse = provider.FindComponents<MudSwitch<bool>>().SingleOrDefault(s => s.Instance.Label == NeverReuseLabel);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(neverReuse, Is.Not.Null, "\"number if taken\" offers the choice");
            Assert.That(mapping().Generation!.NeverReuse, Is.True, "on by default");
        }
    }

    [Test]
    public async Task RandomToken_OffersTheNeverReuseSwitch()
    {
        var (provider, tab, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var tokenRadios = provider.FindComponents<MudRadioGroup<GeneratedValueTokenKind>>().Single();
        await provider.InvokeAsync(() => tokenRadios.Instance.ValueChanged.InvokeAsync(GeneratedValueTokenKind.Random));
        tab.Render();

        Assert.That(provider.FindComponents<MudSwitch<bool>>().Any(s => s.Instance.Label == NeverReuseLabel), Is.True);
    }

    [Test]
    public async Task SequenceToken_HasNoNeverReuseSwitch()
    {
        // A Sequence never reuses a value whatever the setting, so it keeps its locked, always-on line instead.
        var (provider, tab, _) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var tokenRadios = provider.FindComponents<MudRadioGroup<GeneratedValueTokenKind>>().Single();
        await provider.InvokeAsync(() => tokenRadios.Instance.ValueChanged.InvokeAsync(GeneratedValueTokenKind.Sequence));
        tab.Render();

        Assert.That(provider.FindComponents<MudSwitch<bool>>().Any(s => s.Instance.Label == NeverReuseLabel), Is.False);
    }

    [Test]
    public async Task TurningNeverReuseOff_IsWrittenToTheMapping()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);
        await TypeBaseExpressionAsync(provider, tab, "Lower(cs[\"firstName\"])");

        var neverReuse = provider.FindComponents<MudSwitch<bool>>().Single(s => s.Instance.Label == NeverReuseLabel);
        await provider.InvokeAsync(() => neverReuse.Instance.ValueChanged.InvokeAsync(false));
        tab.Render();

        Assert.That(mapping().Generation!.NeverReuse, Is.False);
    }

    [Test]
    public async Task SwitchingToSequenceAfterTurningNeverReuseOff_TurnsItBackOn()
    {
        // The stored flag should say what the engine does: a Sequence never reuses, so leaving it false would make
        // the REST and PowerShell surfaces report something the flow does not do.
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);
        await TypeBaseExpressionAsync(provider, tab, "Lower(cs[\"firstName\"])");

        var neverReuse = provider.FindComponents<MudSwitch<bool>>().Single(s => s.Instance.Label == NeverReuseLabel);
        await provider.InvokeAsync(() => neverReuse.Instance.ValueChanged.InvokeAsync(false));
        var tokenRadios = provider.FindComponents<MudRadioGroup<GeneratedValueTokenKind>>().Single();
        await provider.InvokeAsync(() => tokenRadios.Instance.ValueChanged.InvokeAsync(GeneratedValueTokenKind.Sequence));
        tab.Render();

        Assert.That(mapping().Generation!.NeverReuse, Is.True);
    }

    // ─── The token a new Generated Value starts with ───

    [Test]
    public async Task NewGeneratedValue_NoBaseExpression_StartsOnASequenceNumber()
    {
        // "Only if taken" needs a base expression, so a new Generated Value left on it opened with that radio selected
        // and disabled, and a save error showing before anything had been typed.
        var (_, _, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        Assert.That(mapping().Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.Sequence));
    }

    [Test]
    public async Task NewGeneratedValue_TypingABaseExpression_MovesToOnlyIfTaken()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        await TypeBaseExpressionAsync(provider, tab, "Lower(cs[\"firstName\"])");

        Assert.That(mapping().Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.OnlyIfTaken));
    }

    [Test]
    public async Task NewGeneratedValue_ClearingTheBaseExpression_MovesBackToASequenceNumber()
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        await TypeBaseExpressionAsync(provider, tab, "Lower(cs[\"firstName\"])");
        await TypeBaseExpressionAsync(provider, tab, "");

        Assert.That(mapping().Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.Sequence));
    }

    [TestCase(GeneratedValueTokenKind.Sequence)]
    [TestCase(GeneratedValueTokenKind.Random)]
    public async Task NewGeneratedValue_TokenChosenByTheAdministrator_IsKeptWhenABaseExpressionIsTyped(GeneratedValueTokenKind chosen)
    {
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedTargetSelectedAsync(AttributeDataType.Text);

        var tokenRadios = provider.FindComponents<MudRadioGroup<GeneratedValueTokenKind>>().Single();
        await provider.InvokeAsync(() => tokenRadios.Instance.ValueChanged.InvokeAsync(chosen));
        await TypeBaseExpressionAsync(provider, tab, "Lower(cs[\"firstName\"])");

        Assert.That(mapping().Generation!.TokenKind, Is.EqualTo(chosen));
    }

    [Test]
    public async Task ExistingGeneratedValue_TypingABaseExpression_KeepsItsToken()
    {
        var rule = BuildRule();
        var target = rule.MetaverseObjectType!.Attributes.Single(a => a.Type == AttributeDataType.Text);
        var existing = new SyncRuleMapping
        {
            Id = 101,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Generation = new SyncRuleMappingGeneration { Id = 900, TokenKind = GeneratedValueTokenKind.Sequence }
        };
        rule.AttributeFlowRules.Add(existing);
        var provider = Render<MudDialogProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, rule));
        var edit = typeof(SyncRuleAttributeFlowTab).GetMethod("HandleEditAttributeFlowRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await tab.InvokeAsync(() => (Task)edit.Invoke(tab.Instance, [existing])!);
        tab.Render();
        provider.WaitForAssertion(() => Assert.That(provider.HasComponent<MudRadioGroup<GeneratedValueTokenKind>>(), Is.True));

        await TypeBaseExpressionAsync(provider, tab, "Lower(cs[\"firstName\"])");

        Assert.That(existing.Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.Sequence),
            "a saved choice is the administrator's, whatever the base expression does");
    }

    // ─── Test scaffolding ───

    private static async Task TypeBaseExpressionAsync(IRenderedComponent<MudDialogProvider> provider, IRenderedComponent<SyncRuleAttributeFlowTab> tab, string expression)
    {
        var baseExpression = provider.FindComponents<ExpressionEditor>().Single(f => f.Instance.Label == "Base expression");
        await provider.InvokeAsync(() => baseExpression.Instance.ValueChanged.InvokeAsync(expression));
        tab.Render();
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleAttributeFlowTab> Tab, Func<SyncRuleMapping> Mapping)>
        OpenAddDialogWithGeneratedSourceTypeAsync()
    {
        return await OpenAddDialogWithGeneratedSourceTypeAsync(BuildRule());
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleAttributeFlowTab> Tab, Func<SyncRuleMapping> Mapping)>
        OpenAddDialogWithGeneratedSourceTypeAsync(SyncRule rule)
    {
        var provider = Render<MudDialogProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, rule));

        await tab.FindAll("button").First(b => b.TextContent.Contains("Add Attribute Flow")).ClickAsync();

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
        var (provider, tab, mapping) = await OpenAddDialogWithGeneratedSourceTypeAsync(rule);

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
