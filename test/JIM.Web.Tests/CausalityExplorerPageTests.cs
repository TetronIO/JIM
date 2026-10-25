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
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.TestSupport;
using JIM.Web.Causality;
using JIM.Web.Pages;
using JIM.Web.Services;
using JIM.Web.Shared.Causality;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Causality Explorer page (#2100): what it shows for each kind of focus, and re-centring.
/// </summary>
/// <remarks>
/// Re-centring is only observable here: following a link inside the explorer re-parameterises the same page instance
/// rather than loading a new one, so the page must read the new focus, and only the new focus, every time its
/// parameters change. This is the second sanctioned page-level exception in <c>test/CLAUDE.md</c>, beside the item page.
/// </remarks>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class CausalityExplorerPageTests : JimComponentTestContext
{
    private static readonly Guid AccountId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid GroupId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly Guid ItemId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private NavigationManager _navigation = null!;
    private readonly List<CausalTimelineSubject> _timelineReads = [];

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    [SetUp]
    public void SetUp()
    {
        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        var metaverseRepository = new Mock<IMetaverseRepository>();

        _activityRepository
            .Setup(r => r.GetCausalTimelinePageAsync(It.IsAny<CausalTimelineSubject>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((CausalTimelineSubject subject, int _, int _) =>
            {
                _timelineReads.Add(subject);
                return ([Entry(Guid.NewGuid(), "Export")], 1);
            });
        _activityRepository
            .Setup(r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _activityRepository
            .Setup(r => r.GetLatestImportEventsForItemsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _activityRepository
            .Setup(r => r.GetCausalTimelineEntriesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids.ToDictionary(id => id, id => Entry(id, "Full Synchronisation")));

        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsCoreByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids.Select(Account).ToList());
        _connectedSystemRepository
            .Setup(r => r.GetDeletedCsoChangesByObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        metaverseRepository
            .Setup(r => r.GetMetaverseObjectsCoreByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        metaverseRepository
            .Setup(r => r.GetDeletedMvoChangesByObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        repository.Setup(r => r.Metaverse).Returns(metaverseRepository.Object);
        repository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));

        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    [Test]
    public void Render_NoFocus_SaysWhereToStartFrom()
    {
        _navigation.NavigateTo("/causality");

        var cut = Render<CausalityExplorer>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-causality-start").TextContent, Does.Contain("Explore causality"));
            Assert.That(cut.HasComponent<CausalityTrailBar>(), Is.False);
        }
    }

    [Test]
    public void Render_ObjectFocus_ShowsWhatHappenedToIt()
    {
        var cut = RenderOn($"/causality/connected-system-objects/{AccountId}", ps => ps.Add(c => c.ConnectedSystemObjectId, AccountId));

        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<CausalityObjectTimeline>(), Is.True));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponent<CausalityObjectTimeline>().Instance.Rows, Has.Count.EqualTo(1));
            Assert.That(cut.Find(".jim-causality-focus-what").TextContent, Does.Contain($"name-{AccountId:N}"[..10]));
            Assert.That(_timelineReads, Is.EqualTo(new[] { new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, AccountId) }));
        }
    }

    /// <summary>
    /// Following a link inside the explorer re-parameterises this page: it must read the new focus, show it, and carry
    /// the trail back to where it came from.
    /// </summary>
    [Test]
    public void ReCentre_OnAnotherObject_ReadsAndShowsTheNewFocusWithTheTrailBack()
    {
        var cut = RenderOn($"/causality/connected-system-objects/{AccountId}", ps => ps.Add(c => c.ConnectedSystemObjectId, AccountId));
        cut.WaitForAssertion(() => Assert.That(_timelineReads, Has.Count.EqualTo(1)));

        _navigation.NavigateTo($"/causality/connected-system-objects/{GroupId}?trail=c:{AccountId}");
        cut.Render(ps => ps.Add(c => c.ConnectedSystemObjectId, GroupId));

        cut.WaitForAssertion(() => Assert.That(_timelineReads, Has.Count.EqualTo(2)));
        var crumb = cut.Find("a.jim-causality-crumb");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_timelineReads[^1], Is.EqualTo(new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, GroupId)));
            Assert.That(crumb.GetAttribute("href"), Is.EqualTo($"/causality/connected-system-objects/{AccountId}"));
            Assert.That(cut.Find(".jim-causality-trail [aria-current='page']").TextContent, Does.Contain($"name-{GroupId:N}"[..10]));
        }
    }

    [Test]
    public void Render_ItemFocus_ShowsTheLineageAloneWithLinksThatReCentreTheExplorer()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ItemId, ActivityId = Guid.NewGuid() };
        _activityRepository.Setup(r => r.GetActivityRunProfileExecutionItemDetailAsync(ItemId)).ReturnsAsync(item);

        var cut = RenderOn($"/causality/items/{ItemId}?trail=c:{AccountId}", ps => ps.Add(c => c.ItemId, ItemId));

        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<CausalityPanel>(), Is.True));
        var panel = cut.FindComponent<CausalityPanel>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.LineageOnly, Is.True);
            Assert.That(panel.Context.Links, Is.EqualTo(new CausalityExplorerLinks(
                CausalityTrail.Parse($"c:{AccountId}", new CausalityFocus(CausalityFocusKind.Item, ItemId)),
                new CausalityFocus(CausalityFocusKind.Item, ItemId))));
        }
    }

    [Test]
    public void Render_ItemNoLongerRetained_SaysSoAndShowsNoPanel()
    {
        _activityRepository.Setup(r => r.GetCausalTimelineEntriesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, CausalTimelineEntry>());

        var cut = RenderOn($"/causality/items/{ItemId}", ps => ps.Add(c => c.ItemId, ItemId));

        cut.WaitForAssertion(() => Assert.That(cut.Find(".jim-causality-focus-what").TextContent, Does.Contain("no longer retained")));
        Assert.That(cut.HasComponent<CausalityPanel>(), Is.False);
    }

    private IRenderedComponent<CausalityExplorer> RenderOn(string uri, Action<ComponentParameterCollectionBuilder<CausalityExplorer>> parameters)
    {
        _navigation.NavigateTo(uri);
        return Render(parameters);
    }

    private static ConnectedSystemObject Account(Guid id)
    {
        var nameAttribute = new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "displayName" };
        return new ConnectedSystemObject
        {
            Id = id,
            ConnectedSystemId = 4,
            ConnectedSystem = new ConnectedSystem { Id = 4, Name = "Glitterband EMEA" },
            Type = new ConnectedSystemObjectType { Name = "person" },
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = nameAttribute, AttributeId = 12, StringValue = $"name-{id:N}" }]
        };
    }

    private static CausalTimelineEntry Entry(Guid itemId, string run) => new()
    {
        RunProfileExecutionItemId = itemId,
        Executed = DateTime.UtcNow.AddHours(-1),
        ActivityTargetType = ActivityTargetType.ConnectedSystemRunProfile,
        ActivityTargetName = run,
        ActivityTargetContext = "Glitterband EMEA",
        ObjectChangeType = ObjectChangeType.Exported,
        DisplayName = "tina.adams99",
        ObjectType = "person"
    };

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
