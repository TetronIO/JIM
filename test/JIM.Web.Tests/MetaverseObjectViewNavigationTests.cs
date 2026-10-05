// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Web.Pages.Types;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Metaverse Object page is one component instance for every object the reader moves between: following a
/// reference from one person to another changes the route's id and nothing else. Everything the page loaded about
/// the first person must therefore give way to the second's, on whichever tab the reader is looking at, including
/// loads for the first person that are still running when the reader moves on.
/// </summary>
[TestFixture]
public class MetaverseObjectViewNavigationTests : JimComponentTestContext
{
    private const string TypeUrlParam = "people";

    private static readonly Guid AlphaId = Guid.NewGuid();
    private static readonly Guid BravoId = Guid.NewGuid();

    private Mock<IMetaverseRepository> _metaverse = null!;
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<IActivityRepository> _activities = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _activities = new Mock<IActivityRepository>();
        var sync = new Mock<ISyncRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Activity).Returns(_activities.Object);

        _metaverse
            .Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync(It.IsAny<string>(), false))
            .ReturnsAsync(new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" });
        _metaverse
            .Setup(r => r.GetGeneratedValueOwnershipsAsync(It.IsAny<Guid>()))
            .ReturnsAsync([]);
        SetupObject(AlphaId, connectorCount: 2, createdBy: "Alpha Administrator");
        SetupObject(BravoId, connectorCount: 1, createdBy: null);

        sync.Setup(r => r.GetRetiredGeneratedValueHeadersForObjectAsync(It.IsAny<Guid>())).ReturnsAsync([]);
        sync.Setup(r => r.GetPendingPasswordChangeHeadersAsync(
                It.IsAny<PendingPasswordChangeFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<PendingPasswordChangeHeader>());

        _connectedSystems
            .Setup(r => r.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, PendingExport>());
        _connectedSystems
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync([]);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object, sync.Object));

        var authorisation = AddAuthorization();
        authorisation.SetAuthorized("administrator");
        authorisation.SetRoles("User", "Administrator");
    }

    [SetUp]
    public void SetUp()
    {
        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    private void SetupObject(Guid id, int connectorCount, string? createdBy)
    {
        _metaverse
            .Setup(r => r.GetMetaverseObjectDetailAsync(id, MvoAttributeLoadStrategy.CappedMva))
            .ReturnsAsync(new MvoDetailResult
            {
                MetaverseObject = new MetaverseObject
                {
                    Id = id,
                    Type = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" },
                    Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                ChangeCount = 1,
                ConnectorCount = connectorCount,
                EarliestChangeInitiator = createdBy == null
                    ? null
                    : new MvoChangeInitiatorSummary { InitiatedByType = ActivityInitiatorType.NotSet, InitiatedByName = createdBy }
            });
    }

    private void SetupChangeHistory(Guid id, string initiatorName) =>
        SetupChangeHistory(id, Task.FromResult(BuildChangeHistory(initiatorName)));

    private void SetupChangeHistory(Guid id, Task<(List<MvoChangeHistoryDto> Items, int TotalCount)> history) =>
        _metaverse
            .Setup(r => r.GetMvoChangeHistoryAsync(id, It.IsAny<int>(), It.IsAny<int>()))
            .Returns(history);

    private static (List<MvoChangeHistoryDto> Items, int TotalCount) BuildChangeHistory(string initiatorName) =>
        ([new MvoChangeHistoryDto
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            InitiatedByName = initiatorName
        }], 1);

    private void SetupConnections(Guid id, params string[] connectedSystemNames) =>
        SetupConnections(id, Task.FromResult(BuildConnections(connectedSystemNames)));

    private void SetupConnections(Guid id, Task<List<ConnectedSystemObject>> connections) =>
        _connectedSystems
            .Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(id))
            .Returns(connections);

    private static List<ConnectedSystemObject> BuildConnections(params string[] connectedSystemNames) =>
        connectedSystemNames
            .Select((name, i) => new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = i + 1,
                ConnectedSystem = new ConnectedSystem { Id = i + 1, Name = name },
                TypeId = 1,
                Type = new ConnectedSystemObjectType { Id = 1, Name = "user" },
                Status = ConnectedSystemObjectStatus.Normal
            })
            .ToList();

    private void SetupPasswordEvents(Guid id, string message) =>
        _activities
            .Setup(r => r.GetPasswordSynchronisationEventsAsync(id, It.IsAny<int>()))
            .ReturnsAsync([new PasswordSynchronisationEvent { ActivityId = Guid.NewGuid(), Message = message }]);

    /// <summary>
    /// Opens the page on one object and tab, as a deep link does: the tabs read <c>?t=</c> on their first render.
    /// </summary>
    private IRenderedComponent<View> RenderObject(Guid id, string tab)
    {
        _navigation.NavigateTo($"/t/{TypeUrlParam}/v/{id}?t={tab}");
        return Render<View>(p => p
            .Add(c => c.TypeNameUrlParam, TypeUrlParam)
            .Add(c => c.IdParam, id));
    }

    /// <summary>
    /// Moves the already-open page to another object on the same tab, as the router does: the location changes,
    /// then the same component instance is given the new id.
    /// </summary>
    private void ShowObject(IRenderedComponent<View> page, Guid id, string tab)
    {
        _navigation.NavigateTo($"/t/{TypeUrlParam}/v/{id}?t={tab}");
        page.Render(p => p.Add(c => c.IdParam, id));
    }

    private static IEnumerable<string?> TimelineInitiators(IRenderedComponent<View> page) =>
        page.FindComponent<ChangeHistoryTimeline>().Instance.Changes.Select(c => c.ChangeInitiatorName);

    private static IEnumerable<string> ConnectedSystemNames(IRenderedComponent<View> page) =>
        page.FindComponent<MetaverseObjectConnectionsTable>().Instance.Connections.Select(c => c.ConnectedSystemName);

    private static object? ConnectionsBadge(IRenderedComponent<View> page) =>
        page.FindComponents<MudTabPanel>().Single(p => p.Instance.Text == "Connections").Instance.BadgeData;

    [Test]
    public void ChangesTab_NavigatingToAnotherObjectOnTheSameTab_ShowsOnlyThatObjectsHistory()
    {
        SetupChangeHistory(AlphaId, "Alpha change");
        SetupChangeHistory(BravoId, "Bravo change");
        var page = RenderObject(AlphaId, "changes");
        page.WaitForAssertion(() => Assert.That(TimelineInitiators(page), Is.EqualTo(new[] { "Alpha change" })));

        ShowObject(page, BravoId, "changes");

        page.WaitForAssertion(() => Assert.That(TimelineInitiators(page), Is.EqualTo(new[] { "Bravo change" })));
    }

    [Test]
    public void ChangesTab_PreviousObjectsHistoryArrivingAfterNavigation_IsNotShown()
    {
        var alphaHistory = new TaskCompletionSource<(List<MvoChangeHistoryDto> Items, int TotalCount)>();
        SetupChangeHistory(AlphaId, alphaHistory.Task);
        SetupChangeHistory(BravoId, "Bravo change");
        var page = RenderObject(AlphaId, "changes");

        ShowObject(page, BravoId, "changes");
        page.InvokeAsync(() => alphaHistory.SetResult(BuildChangeHistory("Alpha change")));

        page.WaitForAssertion(() => Assert.That(TimelineInitiators(page), Is.EqualTo(new[] { "Bravo change" })));
    }

    [Test]
    public void ConnectionsTab_NavigatingToAnotherObjectOnTheSameTab_ShowsOnlyThatObjectsConnections()
    {
        SetupConnections(AlphaId, "Alpha HR", "Alpha Directory");
        SetupConnections(BravoId, "Bravo HR");
        var page = RenderObject(AlphaId, "connections");
        page.WaitForAssertion(() => Assert.That(ConnectedSystemNames(page), Is.EquivalentTo(new[] { "Alpha HR", "Alpha Directory" })));

        ShowObject(page, BravoId, "connections");

        page.WaitForAssertion(() => Assert.That(ConnectedSystemNames(page), Is.EqualTo(new[] { "Bravo HR" })));
    }

    [Test]
    public void ConnectionsTab_PreviousObjectsConnectionsArrivingAfterNavigation_AreNotShownOrCounted()
    {
        var alphaConnections = new TaskCompletionSource<List<ConnectedSystemObject>>();
        SetupConnections(AlphaId, alphaConnections.Task);
        SetupConnections(BravoId, "Bravo HR");
        var page = RenderObject(AlphaId, "connections");

        ShowObject(page, BravoId, "connections");
        page.InvokeAsync(() => alphaConnections.SetResult(BuildConnections("Alpha HR", "Alpha Directory")));

        page.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ConnectedSystemNames(page), Is.EqualTo(new[] { "Bravo HR" }));
                Assert.That(ConnectionsBadge(page), Is.EqualTo("1"), "the badge counts the object on screen");
            }
        });
    }

    [Test]
    public void PasswordTab_NavigatingToAnotherObjectOnTheSameTab_LoadsThatObjectsPasswordHistory()
    {
        SetupPasswordEvents(AlphaId, "Alpha password change");
        SetupPasswordEvents(BravoId, "Bravo password change");
        var page = RenderObject(AlphaId, "password");
        page.WaitForAssertion(() => Assert.That(
            page.FindComponent<MetaverseObjectPasswordPanel>().Instance.Events.Select(e => e.Message),
            Is.EqualTo(new[] { "Alpha password change" })));

        ShowObject(page, BravoId, "password");

        page.WaitForAssertion(() =>
        {
            var panel = page.FindComponent<MetaverseObjectPasswordPanel>().Instance;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(panel.Loading, Is.False);
                Assert.That(panel.Events.Select(e => e.Message), Is.EqualTo(new[] { "Bravo password change" }));
            }
        });
    }

    [Test]
    public void PropertiesTab_NavigatingToAnObjectWithNoRecordedCreator_DoesNotShowThePreviousObjectsCreator()
    {
        var page = RenderObject(AlphaId, "properties");
        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Contain("Alpha Administrator")));

        ShowObject(page, BravoId, "properties");

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Not.Contain("Alpha Administrator")));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository, ISyncRepository syncRepository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository, syncRepository: syncRepository);
    }
}
