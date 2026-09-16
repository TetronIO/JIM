// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Web.Pages.Admin;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Pins the exact wording of the vocabulary fix (#1667, #1668) on the pages named as its worked examples:
/// the Pending Exports list writes "Source Metaverse Object" and "Target Connected System Object" in full on
/// its column headers, the Deleted Objects tabs write "Deleted Connected System Objects" and "Deleted Metaverse
/// Objects" in full, and the Connector Space list's Pending Export tooltip says what it means rather than
/// "not yet confirmed". See engineering/DEVELOPER_GUIDE.md > Vocabulary for the full map.
/// <para>
/// These are page-level renders (the standing exception in test/CLAUDE.md is the causality panel; this suite
/// adds a second reason to render a page directly, alongside it, because the strings under test live in a
/// page's own markup rather than in a component under Shared). Each render mocks only the repository calls the
/// page makes on first load, returning empty result sets: the labels under test do not depend on row data.
/// </para>
/// </summary>
[TestFixture]
public class VocabularyLabelsTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 11;

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<IMetaverseRepository> _metaverse = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);

        _connectedSystems
            .Setup(r => r.GetConnectedSystemHeaderAsync(ConnectedSystemId))
            .ReturnsAsync(new ConnectedSystemHeader { Id = ConnectedSystemId, Name = "Directory" });

        _connectedSystems
            .Setup(r => r.GetConnectedSystemHeadersAsync())
            .ReturnsAsync(new List<ConnectedSystemHeader> { new() { Id = ConnectedSystemId, Name = "Directory" } });

        _metaverse
            .Setup(r => r.GetMetaverseObjectTypeHeadersAsync())
            .ReturnsAsync(new List<MetaverseObjectTypeHeader>());

        _connectedSystems
            .Setup(r => r.GetObjectTypesAsync(ConnectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemObjectType>());

        _connectedSystems
            .Setup(r => r.GetPendingExportHeadersRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IEnumerable<PendingExportStatus>?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<PendingExportHeader> { Results = [], TotalResults = 0 });

        _connectedSystems
            .Setup(r => r.GetConnectedSystemObjectHeadersRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<IEnumerable<ConnectedSystemObjectStatus>?>(),
                It.IsAny<IEnumerable<int>?>(), It.IsAny<IEnumerable<ConnectedSystemObjectJoinType>?>(),
                It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<ConnectedSystemObjectHeader>
            {
                Results =
                [
                    new ConnectedSystemObjectHeader
                    {
                        Id = Guid.NewGuid(),
                        ConnectedSystemId = ConnectedSystemId,
                        TypeId = 1,
                        TypeName = "User",
                        PendingExternalId = "CN=pending,DC=example,DC=com"
                    }
                ],
                TotalResults = 1
            });

        _connectedSystems
            .Setup(r => r.GetDeletedCsoChangesRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<ConnectedSystemObjectChange> { Results = [], TotalResults = 0 });

        _metaverse
            .Setup(r => r.GetDeletedMvoChangesRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<MetaverseObjectChange> { Results = [], TotalResults = 0 });

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [SetUp]
    public void SetUp()
    {
        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    [Test]
    public void PendingExportList_ColumnHeaders_SpellOutMetaverseAndConnectedSystemObjectInFull()
    {
        _navigation.NavigateTo($"/admin/connected-systems/{ConnectedSystemId}/pending-exports");

        var cut = Render<PendingExportList>(p => p.Add(c => c.Id, ConnectedSystemId));

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("Source Metaverse Object"),
                    "the source column must name the Metaverse Object in full, not \"Source MVO\"");
                Assert.That(cut.Markup, Does.Contain("Target Connected System Object"),
                    "the target column must name the Connected System Object in full, not \"Target Object\"");
                Assert.That(cut.Markup, Does.Not.Contain("Source MVO"));
                Assert.That(cut.Markup, Does.Not.Contain(">Target Object<"));
            }
        });
    }

    [Test]
    public void DeletedObjects_Tabs_SpellOutConnectedSystemAndMetaverseObjectsInFull()
    {
        _navigation.NavigateTo("/admin/deleted-objects");

        var cut = Render<DeletedObjects>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("Deleted Connected System Objects"),
                    "the first tab must read \"Deleted Connected System Objects\", not \"Deleted CSOs\"");
                Assert.That(cut.Markup, Does.Contain("Deleted Metaverse Objects"),
                    "the second tab must read \"Deleted Metaverse Objects\", not \"Deleted MVOs\"");
                Assert.That(cut.Markup, Does.Not.Contain("Deleted CSOs"));
                Assert.That(cut.Markup, Does.Not.Contain("Deleted MVOs"));
            }
        });
    }

    [Test]
    public void ConnectorSpace_PendingExternalId_TooltipExplainsWhatAPendingExportIs()
    {
        _navigation.NavigateTo($"/admin/connected-systems/{ConnectedSystemId}/connector-space");

        var cut = Render<JIM.Web.Pages.Admin.ConnectedSystemObjectList>(p => p.Add(c => c.Id, ConnectedSystemId));

        // MudTooltip renders its explanatory text into the popover provider's own render tree, not inline in
        // this component's markup, so the assertion reads the component's Text parameter directly rather than
        // scanning rendered HTML (the pattern used by ObjectChipTests for the same reason).
        cut.WaitForAssertion(() =>
        {
            var tooltipTexts = cut.FindComponents<MudTooltip>().Select(t => t.Instance.Text).ToList();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tooltipTexts, Has.Some.EqualTo("Pending Export awaiting confirmation"),
                    "the tooltip must say what a Pending Export on this cell means");
                Assert.That(tooltipTexts, Has.None.EqualTo("Pending Export - not yet confirmed"));
            }
        });
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
