// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestSupport;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Attribute Flow list's way into the retired values register (Unique Value Generation, #242, Phase 6, mockup
/// section A): an "N retired" chip on a generated row that has any, and a "View retired values" action on every
/// generated row. The counts are read once per page for every generated row together, never once per row.
/// </summary>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class SyncRuleAttributeFlowTabRetiredValuesTests : JimComponentTestContext
{
    private const string RetiredChipMarker = "jim-retired-count";
    private const int AccountNameAttributeId = 1;
    private const int EmployeeNumberAttributeId = 2;
    private const int DepartmentAttributeId = 3;

    private Mock<ISyncRepository> _syncRepository = null!;
    private List<RetiredGeneratedValueCount> _counts = [];

    protected override void ConfigureAdditionalServices()
    {
        _syncRepository = new Mock<ISyncRepository>();
        _syncRepository
            .Setup(r => r.GetRetiredGeneratedValueCountsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(() => _counts);

        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ConnectedSystems).Returns(new Mock<IConnectedSystemRepository>().Object);
        repo.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        Services.AddSingleton<IJimApplicationFactory>(
            new FakeJimApplicationFactory(new JimApplication(repo.Object, syncRepository: _syncRepository.Object)));
    }

    [Test]
    public void SyncRuleAttributeFlowTab_GeneratedRowWithRetiredValues_ShowsTheRetiredCount()
    {
        _counts = [new RetiredGeneratedValueCount { MetaverseAttributeId = AccountNameAttributeId, Count = 12 }];

        var tab = RenderTab();

        tab.WaitForAssertion(() =>
        {
            var chips = tab.FindAll($"[data-testid='{RetiredChipMarker}']");
            Assert.That(chips.Select(c => c.TextContent.Trim()), Is.EqualTo(new[] { "12 retired" }),
                "only the generated row whose attribute has retired values carries the chip");
        });
    }

    [Test]
    public void SyncRuleAttributeFlowTab_NoRetiredValues_ShowsNoRetiredChip()
    {
        _counts = [];

        var tab = RenderTab();

        tab.WaitForAssertion(() => Assert.That(tab.FindAll("[aria-label='view retired values']"), Is.Not.Empty));
        Assert.That(tab.FindAll($"[data-testid='{RetiredChipMarker}']"), Is.Empty, "a zero count is not shown");
    }

    [Test]
    public void SyncRuleAttributeFlowTab_GeneratedRows_EachOfferViewRetiredValuesAndOtherRowsDoNot()
    {
        var tab = RenderTab();

        tab.WaitForAssertion(() => Assert.That(tab.FindAll("[aria-label='view retired values']"), Has.Count.EqualTo(2),
            "both generated rows offer the action, with or without retired values; the plain Attribute row does not"));
    }

    [Test]
    public void SyncRuleAttributeFlowTab_Opens_ReadsEveryGeneratedRowsCountInOneQuery()
    {
        var tab = RenderTab();

        tab.WaitForAssertion(() => Assert.That(tab.FindAll("[aria-label='view retired values']"), Is.Not.Empty));
        _syncRepository.Verify(r => r.GetRetiredGeneratedValueCountsAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { AccountNameAttributeId, EmployeeNumberAttributeId })),
                It.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0)),
            Times.Once,
            "one grouped read for the generated rows' attributes, not one per row and not for plain flows");
    }

    private IRenderedComponent<SyncRuleAttributeFlowTab> RenderTab()
        => Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, BuildRule()));

    private static SyncRule BuildRule()
    {
        var accountName = new MetaverseAttribute { Id = AccountNameAttributeId, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var employeeNumber = new MetaverseAttribute { Id = EmployeeNumberAttributeId, Name = "Employee Number", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var department = new MetaverseAttribute { Id = DepartmentAttributeId, Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var metaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User" };
        metaverseObjectType.Attributes.AddRange([accountName, employeeNumber, department]);

        var departmentSource = new ConnectedSystemObjectTypeAttribute { Id = 5, Name = "department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var connectedSystemObjectType = new ConnectedSystemObjectType { Id = 2, Name = "user" };
        connectedSystemObjectType.Attributes.Add(departmentSource);

        var rule = new SyncRule
        {
            Id = 4,
            Name = "HR Import",
            Direction = SyncRuleDirection.Import,
            MetaverseObjectType = metaverseObjectType,
            ConnectedSystemObjectType = connectedSystemObjectType
        };

        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 101,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = accountName,
            TargetMetaverseAttributeId = accountName.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "Lower(cs[\"firstName\"])" } },
            Generation = new SyncRuleMappingGeneration { Id = 900, TokenKind = GeneratedValueTokenKind.OnlyIfTaken, NeverReuse = true }
        });
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 102,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = employeeNumber,
            TargetMetaverseAttributeId = employeeNumber.Id,
            Generation = new SyncRuleMappingGeneration { Id = 901, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1 }
        });
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 103,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = department,
            TargetMetaverseAttributeId = department.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = departmentSource, ConnectedSystemAttributeId = departmentSource.Id } }
        });
        return rule;
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
