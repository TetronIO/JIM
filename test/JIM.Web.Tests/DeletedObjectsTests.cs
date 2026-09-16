// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using JIM.Web.Pages.Admin;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Deleted Objects grids naming what a deleted object is and where it lived beneath its
/// Display Name (#1669): a deleted Connected System Object row says its type and Connected System, and
/// a deleted Metaverse Object row says its type, using <see cref="ObjectDescription"/> rather than the
/// separate "Object Type" column the page used to carry.
/// </summary>
[TestFixture]
public class DeletedObjectsTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 3;

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<IMetaverseRepository> _metaverse = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);

        _connectedSystems
            .Setup(r => r.GetConnectedSystemHeadersAsync())
            .ReturnsAsync(new List<ConnectedSystemHeader>
            {
                new() { Id = ConnectedSystemId, Name = "Panoply AD" }
            });
        _metaverse
            .Setup(r => r.GetMetaverseObjectTypeHeadersAsync())
            .ReturnsAsync(new List<MetaverseObjectTypeHeader>());

        SetupCsoChanges([]);
        SetupMvoChanges([]);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    private void SetupCsoChanges(List<ConnectedSystemObjectChange> changes)
    {
        _connectedSystems
            .Setup(r => r.GetDeletedCsoChangesRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<ConnectedSystemObjectChange> { Results = changes, TotalResults = changes.Count });
    }

    private void SetupMvoChanges(List<MetaverseObjectChange> changes)
    {
        _metaverse
            .Setup(r => r.GetDeletedMvoChangesRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<MetaverseObjectChange> { Results = changes, TotalResults = changes.Count });
    }

    [Test]
    public void DeletedObjects_DeletedConnectedSystemObjectRow_NamesItsTypeAndConnectedSystem()
    {
        SetupCsoChanges(
        [
            new ConnectedSystemObjectChange
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = ConnectedSystemId,
                ChangeTime = DateTime.UtcNow,
                DeletedObjectDisplayName = "Baseline User",
                DeletedObjectType = new ConnectedSystemObjectType { Name = "user" }
            }
        ]);

        var cut = Render<DeletedObjects>();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("user in Panoply AD"),
            "the row must say what the deleted object is and where it lived, not just its name"));
    }

    /// <summary>
    /// A row whose type was not captured (pre-dates change tracking recording it, say) still names its
    /// Connected System: <see cref="ObjectDescription.ForConnectedSystemObjectPlace"/> owns this
    /// fallback, so the page must not have its own "Unknown" wording to fall out of step with it.
    /// </summary>
    [Test]
    public void DeletedObjects_DeletedConnectedSystemObjectRowWithNoType_StillNamesTheConnectedSystem()
    {
        SetupCsoChanges(
        [
            new ConnectedSystemObjectChange
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = ConnectedSystemId,
                ChangeTime = DateTime.UtcNow,
                DeletedObjectDisplayName = "Baseline User",
                DeletedObjectType = null
            }
        ]);

        var cut = Render<DeletedObjects>();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("in Panoply AD")));
    }

    [Test]
    public void DeletedObjects_DeletedMetaverseObjectRow_NamesItsType()
    {
        SetupMvoChanges(
        [
            new MetaverseObjectChange
            {
                Id = Guid.NewGuid(),
                ChangeTime = DateTime.UtcNow,
                DeletedObjectDisplayName = "Baseline User",
                DeletedObjectType = new MetaverseObjectType { Name = "User" }
            }
        ]);

        var cut = Render<DeletedObjects>();

        // Switch to the Deleted Metaverse Objects tab.
        var tabs = cut.FindAll(".mud-tab");
        tabs[1].Click();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("User in the Metaverse")));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
