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
using JIM.Models.Enums;
using JIM.TestSupport;
using JIM.Web.Pages;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The execution item page's Follow-up section (#2668): work the item set in motion that finishes after its run, such as
/// the Initial Password of the account an export provisioned, listed under the item with a link to each one's Activity.
/// </summary>
/// <remarks>
/// A page-level test, the sanctioned exception in <c>test/CLAUDE.md</c> for this page: the section is fed by the page's
/// own load (the causality loader's follow-up read), so whether it appears is only observable by rendering the page.
/// </remarks>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ExecutionItemPageFollowUpTests : JimComponentTestContext
{
    private static readonly Guid ItemId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000002668");
    private static readonly Guid RunActivityId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000002668");

    private Mock<IActivityRepository> _activityRepository = null!;

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
        AddAuthorization().SetAuthorized("Ada Lovelace").SetRoles("User");
    }

    [SetUp]
    public void SetUp()
    {
        _activityRepository = new Mock<IActivityRepository>();
        _activityRepository.Setup(r => r.GetActivityRunProfileExecutionItemDetailAsync(ItemId)).ReturnsAsync(
            new ActivityRunProfileExecutionItem { Id = ItemId, ActivityId = RunActivityId, ObjectChangeType = ObjectChangeType.Exported });
        _activityRepository.Setup(r => r.GetActivityAsync(RunActivityId)).ReturnsAsync(
            new Activity { Id = RunActivityId, TargetType = ActivityTargetType.ConnectedSystemRunProfile, TargetName = "Export" });
        _activityRepository.Setup(r => r.GetFollowUpActivitiesAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([]);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(new Mock<IConnectedSystemRepository>().Object);
        repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);
        repository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [Test]
    public void Render_ItemWithAFollowUp_ListsItWithALinkToItsActivity()
    {
        var followUpId = Guid.NewGuid();
        _activityRepository.Setup(r => r.GetFollowUpActivitiesAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(ItemId))))
            .ReturnsAsync([new Activity
            {
                Id = followUpId,
                ParentRunProfileExecutionItemId = ItemId,
                TargetType = ActivityTargetType.ConnectedSystem,
                TargetName = "Panoply AD",
                Status = ActivityStatus.Complete,
                Message = "Something followed."
            }]);

        var cut = Render<ActivityRunProfileExecutionItemDetail>(ps => ps.Add(p => p.Id, ItemId));

        cut.WaitForAssertion(() => Assert.That(cut.FindAll("[data-testid='jim-follow-up']"), Has.Count.EqualTo(1)));
        var links = cut.Find("[data-testid='jim-follow-up']").QuerySelectorAll("a").Select(a => a.GetAttribute("href"));
        Assert.That(links, Does.Contain($"/activity/{followUpId}"));
    }

    [Test]
    public void Render_ItemWithNoFollowUps_ShowsNoFollowUpSection()
    {
        var cut = Render<ActivityRunProfileExecutionItemDetail>(ps => ps.Add(p => p.Id, ItemId));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Execution Summary")));
        Assert.That(cut.FindAll("[data-testid='jim-follow-up']"), Is.Empty);
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
