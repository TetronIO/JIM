// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Component tests for <see cref="AttributePriorityList"/>'s read-only mode (#91 Surface 1, #1199): the mapping
/// editor reuses the same control Surface 2 manages priority with, but has to show a contribution that does not
/// exist yet, or point at the one being edited. Those two states are only expressible at component level.
/// </summary>
[TestFixture]
public class AttributePriorityListTests : JimComponentTestContext
{
    private const int ObjectTypeId = 7;
    private const int AttributeId = 42;

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;

    protected override void ConfigureAdditionalServices()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(mockRepository.Object));
    }

    private void SetupContributors(params SyncRuleMapping[] mappings)
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(ObjectTypeId, AttributeId))
            .ReturnsAsync(mappings.ToList());
    }

    private static SyncRuleMapping BuildContributor(int id, string systemName, string ruleName, bool enabled = true) => new()
    {
        Id = id,
        TargetMetaverseAttributeId = AttributeId,
        SyncRule = new SyncRule
        {
            Id = id * 100,
            Name = ruleName,
            Enabled = enabled,
            ConnectedSystem = new ConnectedSystem { Id = id, Name = systemName }
        }
    };

    private IRenderedComponent<AttributePriorityList> RenderReadOnly(Action<ComponentParameterCollectionBuilder<AttributePriorityList>>? extra = null)
    {
        return Render<AttributePriorityList>(p =>
        {
            p.Add(c => c.MetaverseObjectTypeId, ObjectTypeId);
            p.Add(c => c.MetaverseAttributeId, AttributeId);
            p.Add(c => c.MetaverseAttributeName, "department");
            p.Add(c => c.ReadOnly, true);
            extra?.Invoke(p);
        });
    }

    [Test]
    public void ReadOnly_WithPendingContributor_RendersItLastBelowThePersistedContributors()
    {
        // The mapping being created has no row of its own to load, so the control has to draw one: an administrator
        // needs to see where it will land, which is always the bottom.
        SetupContributors(BuildContributor(10, "HR System", "HR People Inbound"),
                          BuildContributor(20, "Self-Service AD", "AD Self-Service Inbound"));

        var cut = RenderReadOnly(p => p.Add(c => c.PendingContributorSystemName, "Corporate Directory"));

        var rows = cut.FindAll(".apt-row");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(3), "the unsaved mapping must appear alongside the persisted contributors");
            Assert.That(rows[2].TextContent, Does.Contain("Corporate Directory"));
            Assert.That(rows[2].TextContent, Does.Contain("this mapping, once saved"));
            Assert.That(rows[2].TextContent, Does.Contain("3"), "it must be numbered last in the list");
        }
    }

    [Test]
    public void ReadOnly_WithPendingContributorAndNoExistingContributors_StillRendersThePendingRow()
    {
        // A sole contributor has no priority to manage, and the control says so. That message must not displace the
        // pending row, or the dialog would claim the attribute has no contributors while adding one.
        SetupContributors();

        var cut = RenderReadOnly(p => p.Add(c => c.PendingContributorSystemName, "Corporate Directory"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".apt-row"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Not.Contain("no import contributors"));
        }
    }

    [Test]
    public void ReadOnly_WithHighlightedMapping_MarksThatRowAsTheOneBeingEdited()
    {
        // Editing a mapping that already contributes: it is in the loaded list, so it is pointed at rather than
        // appended. Appending it as well would show the same contribution twice.
        SetupContributors(BuildContributor(10, "HR System", "HR People Inbound"),
                          BuildContributor(20, "Corporate Directory", "CorpDir People Inbound"));

        var cut = RenderReadOnly(p => p.Add(c => c.HighlightMappingId, 20));

        var rows = cut.FindAll(".apt-row");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(2), "no extra row is drawn for a mapping that already contributes");
            Assert.That(rows[0].TextContent, Does.Not.Contain("this mapping"));
            Assert.That(rows[1].TextContent, Does.Contain("this mapping"));
            Assert.That(rows[1].ClassName, Does.Contain("apt-row-current"));
        }
    }

    [Test]
    public void ReadOnly_RendersNoReorderOrSaveAffordances()
    {
        // Surface 1 is read-only by design: priority is managed in one place, on the Object Type page.
        SetupContributors(BuildContributor(10, "HR System", "HR People Inbound"),
                          BuildContributor(20, "Corporate Directory", "CorpDir People Inbound"));

        var cut = RenderReadOnly();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".apt-drag-handle"), Is.Empty, "there is no reordering on this surface");
            Assert.That(cut.Markup, Does.Not.Contain("Save order"));
        }
    }

    [Test]
    public void ReadOnly_DisabledContributingRule_StillHoldsItsPosition()
    {
        // A disabled rule never contributes during synchronisation but keeps its place, so the ordering stays stable
        // while configuration changes are staged.
        SetupContributors(BuildContributor(10, "HR System", "HR People Inbound"),
                          BuildContributor(20, "Legacy HR", "Legacy HR Inbound", enabled: false));

        var cut = RenderReadOnly();

        var rows = cut.FindAll(".apt-row");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[1].TextContent, Does.Contain("Disabled"));
        }
    }

    [Test]
    public void ReadOnly_SameAttributeReRendered_DoesNotReloadTheContributorList()
    {
        // The control lives inside a mapping dialog that re-renders as the administrator types. Reloading on every
        // render would re-query needlessly, and on Surface 2 would discard a reorder in progress.
        SetupContributors(BuildContributor(10, "HR System", "HR People Inbound"),
                          BuildContributor(20, "Corporate Directory", "CorpDir People Inbound"));

        var cut = RenderReadOnly();
        cut.Render(p => p.Add(c => c.PendingContributorNullIsValue, true));

        _mockConnectedSystemRepo.Verify(
            r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(ObjectTypeId, AttributeId), Times.Once);
    }

    [Test]
    public void ReadOnly_TargetAttributeChanged_ReloadsForTheNewAttribute()
    {
        // The attribute picker sits in the same dialog, so the list's subject changes under it and must follow.
        const int otherAttributeId = 43;
        SetupContributors(BuildContributor(10, "HR System", "HR People Inbound"));
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(ObjectTypeId, otherAttributeId))
            .ReturnsAsync([BuildContributor(30, "Payroll", "Payroll Inbound")]);

        var cut = RenderReadOnly();
        cut.Render(p => p.Add(c => c.MetaverseAttributeId, otherAttributeId));

        Assert.That(cut.FindAll(".apt-row")[0].TextContent, Does.Contain("Payroll"));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
