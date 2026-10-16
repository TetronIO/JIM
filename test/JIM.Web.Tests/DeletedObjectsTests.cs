// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using JIM.Web.Pages.Admin;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
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

    [Test]
    public void DeletedObjects_DeletedByAPerson_LinksTheirMetaverseObject()
    {
        // Deleted By used to link a person to /users/{id}, a route that does not exist (#2066).
        var personId = Guid.NewGuid();
        SetupMvoChanges(
        [
            new MetaverseObjectChange
            {
                Id = Guid.NewGuid(),
                ChangeTime = DateTime.UtcNow,
                DeletedObjectDisplayName = "Jane Smith",
                DeletedObjectType = new MetaverseObjectType { Name = "User" },
                InitiatedByType = ActivityInitiatorType.User,
                InitiatedById = personId,
                InitiatedByName = "Jay Example"
            }
        ]);

        var cut = Render<DeletedObjects>();

        // Deleted By is on the Deleted Metaverse Objects tab.
        cut.FindAll(".mud-tab")[1].Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Find("[data-testid=jim-initiator-user] a").GetAttribute("href"), Is.EqualTo($"/t/users/v/{personId}")));
    }

    /// <summary>
    /// A reference in a deleted Metaverse Object's change history (its manager, say) links to the referenced
    /// Metaverse Object's own page, /t/{plural}/v/{id}. It used to link to /users/{id}, a route that does not
    /// exist (#2146). The referenced object's type is deliberately not the built-in User type, so a link
    /// hard-coded to /t/users/ fails this as surely as the old one did.
    /// </summary>
    [Test]
    public void DeletedObjects_DeletedMetaverseObjectHistoryWithAReference_LinksTheReferencedMetaverseObject()
    {
        var deletionId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var deletion = new MetaverseObjectChange
        {
            Id = deletionId,
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = DateTime.UtcNow,
            DeletedObjectDisplayName = "Jane Smith",
            DeletedObjectType = new MetaverseObjectType { Name = "User" }
        };
        SetupMvoChanges([deletion]);

        var update = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = DateTime.UtcNow.AddDays(-1)
        };
        var managerAttribute = new MetaverseObjectChangeAttribute
        {
            AttributeName = "Manager",
            AttributeType = AttributeDataType.Reference,
            MetaverseObjectChange = update
        };
        var manager = new MetaverseObject
        {
            Id = managerId,
            CachedDisplayName = "Ada Lovelace",
            Type = new MetaverseObjectType { Id = 7, Name = "Person", PluralName = "People" }
        };
        managerAttribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(managerAttribute, ValueChangeType.Add, manager));
        update.AttributeChanges.Add(managerAttribute);
        _metaverse
            .Setup(r => r.GetDeletedMvoChangeHistoryAsync(deletionId))
            .ReturnsAsync([update, deletion]);

        // Both history dialogs (the deleted object's, then the change details inside its timeline) are inline
        // MudDialogs, so their content renders into the provider rather than into the page.
        var provider = Render<MudDialogProvider>();
        var cut = Render<DeletedObjects>();
        cut.FindAll(".mud-tab")[1].Click();

        // Wait only to find the row's View button, then click it once: a click inside a retried wait could open
        // the dialog more than once and would hide which half failed.
        cut.WaitForAssertion(() => Assert.That(FindMvoViewButton(cut), Is.Not.Null));
        FindMvoViewButton(cut)!.Click();

        provider.WaitForAssertion(() => Assert.That(provider.HasComponent<ChangeHistoryTimeline>(), Is.True));
        provider.FindComponent<ChangeHistoryTimeline>()
            .FindAll(".change-card")
            .Single(c => c.TextContent.Contains("Manager"))
            .Click();

        provider.WaitForAssertion(() =>
            Assert.That(provider.FindAll("a").Single(a => a.TextContent.Trim() == "Ada Lovelace").GetAttribute("href"),
                Is.EqualTo($"/t/people/v/{managerId}"),
                "a reference in a deleted object's history must open the referenced Metaverse Object, not /users/{id} (#2146)"));
    }

    private static IElement? FindMvoViewButton(IRenderedComponent<DeletedObjects> cut) =>
        cut.FindComponent<VirtualisedDataGrid<MetaverseObjectChange>>()
            .FindAll("button")
            .FirstOrDefault(b => b.TextContent.Trim() == "View");

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
