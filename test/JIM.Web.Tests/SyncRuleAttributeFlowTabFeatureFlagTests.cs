// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
using JIM.TestSupport;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The "JIM generates it" Source Type option's visibility, gated behind Unique Value Generation's feature flag
/// (#242, Phase 3.5): offered only while the flag is on, but an already-generated mapping still opens for editing
/// and still shows its own Source Type, flag off or on, so existing configuration is never stranded. See
/// <see cref="SyncRuleAttributeFlowGeneratedFormTests"/> for the form itself (run with the flag on throughout, as
/// every non-flag test in this area is).
/// </summary>
[TestFixture]
public class SyncRuleAttributeFlowTabFeatureFlagTests : JimComponentTestContext
{
    private readonly FakeJimApplicationFactory _factory = new();
    private Mock<IConnectedSystemRepository> _csRepo = null!;

    protected override void ConfigureAdditionalServices()
    {
        _csRepo = new Mock<IConnectedSystemRepository>();
        // Registered once, as ConfigureAdditionalServices must be (bUnit locks the service provider against
        // further registration once anything has been resolved from it); BuildJim() below swaps out the
        // JimApplication instance the factory hands back per test, rather than re-registering the factory.
        Services.AddSingleton<IJimApplicationFactory>(_factory);
        // GeneratedValueOptionsEditor's preview panel (rendered once an existing generated mapping's dialog opens)
        // needs one, as SyncRuleAttributeFlowGeneratedFormTests's fixture does.
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Current?.Dispose();
    }

    [Test]
    public void AddDialog_FlagOff_GeneratedSourceTypeOptionIsAbsent()
    {
        BuildJim(flagEnabled: false);
        var (provider, _) = OpenAddDialog();

        var sourceTypePicker = provider.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Source Type");
        var options = sourceTypePicker.FindComponents<MudSelectItem<string>>().Select(i => i.Instance.Value).ToList();

        Assert.That(options, Does.Not.Contain("Generated"),
            "with the flag off, an administrator must not be offered the option to create new generated configuration");
    }

    [Test]
    public void AddDialog_FlagOn_GeneratedSourceTypeOptionIsOffered()
    {
        BuildJim(flagEnabled: true);
        var (provider, _) = OpenAddDialog();

        var sourceTypePicker = provider.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Source Type");
        var options = sourceTypePicker.FindComponents<MudSelectItem<string>>().Select(i => i.Instance.Value).ToList();

        Assert.That(options, Does.Contain("Generated"));
    }

    [Test]
    public void EditDialog_FlagOffOnAnExistingGeneratedMapping_StillShowsGeneratedAsTheSourceType()
    {
        BuildJim(flagEnabled: false);
        var rule = BuildRuleWithExistingGeneratedMapping(out var existingMapping);

        var provider = Render<MudDialogProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, rule));

        // Open the existing mapping for editing, exactly as a row click on the Attribute Flow table would.
        tab.InvokeAsync(() => InvokeEditAttributeFlowRule(tab.Instance, existingMapping)).GetAwaiter().GetResult();
        tab.Render();

        provider.WaitForAssertion(() => Assert.That(provider.HasComponent<MudSelect<string>>(), Is.True));
        var sourceTypePicker = provider.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Source Type");

        // MudBlazor's MUD0012 analyzer forbids external reads of a two-way-bound parameter (Value) via
        // ParameterState; SyncRuleAttributeFlowTabTests and SyncRuleAttributeFlowGeneratedFormTests already read
        // MudSelect this way, per test/CLAUDE.md's bUnit guidance to assert on a component's own parameters.
#pragma warning disable MUD0012
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceTypePicker.Instance.Value, Is.EqualTo("Generated"),
                "the dialog must still open showing the mapping's real Source Type, flag off or on");
            Assert.That(sourceTypePicker.FindComponents<MudSelectItem<string>>().Select(i => i.Instance.Value),
                Does.Contain("Generated"),
                "the option must still render so the selected value resolves to its label, not the raw string");
        }
#pragma warning restore MUD0012
    }

    // ─── scaffolding ───

    private void BuildJim(bool flagEnabled)
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        repo.Setup(r => r.ServiceSettings).Returns(flagEnabled
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        _factory.Current?.Dispose();
        _factory.Current = new JimApplication(repo.Object);
    }

    private (IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleAttributeFlowTab> Tab) OpenAddDialog()
    {
        var provider = Render<MudDialogProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, BuildRule()));

        tab.FindAll("button").First(b => b.TextContent.Contains("Add Attribute Flow")).Click();
        provider.WaitForAssertion(() => Assert.That(provider.HasComponent<MudSelect<string>>(), Is.True));

        return (provider, tab);
    }

    // HandleEditAttributeFlowRuleAsync is private; invoked via the component's own instance through the same
    // reflection approach SyncRuleAttributeFlowGeneratedFormTests uses to read _attributeFlowMapping.
    private static Task InvokeEditAttributeFlowRule(SyncRuleAttributeFlowTab tab, SyncRuleMapping mapping)
    {
        var method = typeof(SyncRuleAttributeFlowTab).GetMethod("HandleEditAttributeFlowRuleAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(tab, [mapping])!;
    }

    private static SyncRule BuildRule()
    {
        var textAttribute = new MetaverseAttribute { Id = 1, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var metaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User" };
        metaverseObjectType.Attributes.Add(textAttribute);

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

    private static SyncRule BuildRuleWithExistingGeneratedMapping(out SyncRuleMapping existingMapping)
    {
        var rule = BuildRule();
        var targetAttribute = rule.MetaverseObjectType!.Attributes.Single();
        existingMapping = new SyncRuleMapping
        {
            Id = 101,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = targetAttribute,
            TargetMetaverseAttributeId = targetAttribute.Id,
            Generation = new SyncRuleMappingGeneration { Id = 900, TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Guid }
        };
        rule.AttributeFlowRules.Add(existingMapping);
        return rule;
    }

    private sealed class FakeJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication? Current { get; set; }
        public JimApplication Create() => Current!;
    }
}
