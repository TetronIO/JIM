// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The notice on a Connected System's page for one Object Type whose objects the next synchronisation will all process
/// (#2615): the changes behind it, and Run now, which is offered only where a Run Profile that applies them exists.
/// </summary>
[TestFixture]
public class AwaitingSynchronisationNoticeTests : JimComponentTestContext
{
    private const string RunNow = "[data-testid='jim-awaiting-synchronisation-run-now']";
    private const string Caption = "[data-testid='jim-awaiting-synchronisation-caption']";
    private const string NoRunProfile = "[data-testid='jim-awaiting-synchronisation-no-run-profile']";
    private const string Truncated = "[data-testid='jim-awaiting-synchronisation-truncated']";

    [Test]
    public void Render_ChangesWaiting_ListsOneRowPerChange()
    {
        var cut = RenderNotice(Entry(changeCount: 3, listed: 3), runProfile: null);

        Assert.That(cut.FindAll("[data-testid='jim-awaiting-synchronisation-changes'] tbody tr"), Has.Count.EqualTo(3));
    }

    [Test]
    public void Render_ChangeWhoseActivityIsGone_ListsItWithoutALink()
    {
        var entry = Entry(changeCount: 1, listed: 1, activityIds: false);

        var cut = RenderNotice(entry, runProfile: null);

        Assert.That(cut.FindAll("[data-testid='jim-awaiting-synchronisation-changes'] tbody a[href^='/activity/']"), Is.Empty);
    }

    [Test]
    public void Render_ChangeWithItsActivity_LinksToIt()
    {
        var entry = Entry(changeCount: 1, listed: 1, activityIds: true);

        var cut = RenderNotice(entry, runProfile: null);

        var activityId = entry.Changes[0].ConfigurationChangeActivityId;
        Assert.That(cut.Find($"a[href='/activity/{activityId}']").TextContent, Does.Contain("Change 1"));
    }

    [Test]
    public void Render_RunProfileThatAppliesTheChanges_OffersRunNowAndNamesTheRunType()
    {
        var cut = RenderNotice(Entry(changeCount: 1, listed: 1), RunProfile(ConnectedSystemRunType.DeltaSynchronisation));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(RunNow), Has.Count.EqualTo(1));
            Assert.That(cut.Find(Caption).TextContent, Does.Contain("Delta Synchronisation"));
            Assert.That(cut.FindAll(NoRunProfile), Is.Empty);
        }
    }

    [Test]
    public void Render_FullSynchronisationRunProfile_NamesTheFullSynchronisationInTheCaption()
    {
        var cut = RenderNotice(Entry(changeCount: 1, listed: 1), RunProfile(ConnectedSystemRunType.FullSynchronisation));

        Assert.That(cut.Find(Caption).TextContent, Does.Contain("Full Synchronisation"));
    }

    [Test]
    public void Render_NoRunProfileThatAppliesTheChanges_OffersNoRunNow()
    {
        var cut = RenderNotice(Entry(changeCount: 1, listed: 1), runProfile: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(RunNow), Is.Empty);
            Assert.That(cut.FindAll(NoRunProfile), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task RunNow_Clicked_RaisesOnRunNowOnceAsync()
    {
        var raised = 0;
        var cut = Render<AwaitingSynchronisationNotice>(p => p
            .Add(c => c.Entry, Entry(changeCount: 1, listed: 1))
            .Add(c => c.RunProfile, RunProfile(ConnectedSystemRunType.DeltaSynchronisation))
            .Add(c => c.OnRunNow, () => raised++));

        await cut.Find(RunNow).ClickAsync();

        Assert.That(raised, Is.EqualTo(1));
    }

    [Test]
    public void Render_MoreChangesThanListed_SaysHowManyAreShown()
    {
        var cut = RenderNotice(Entry(changeCount: 700, listed: 500), runProfile: null);

        var caption = cut.Find(Truncated).TextContent;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(caption, Does.Contain("500"));
            Assert.That(caption, Does.Contain("700"));
        }
    }

    [Test]
    public void Render_EveryChangeListed_SaysNothingAboutHowManyAreShown()
    {
        var cut = RenderNotice(Entry(changeCount: 3, listed: 3), runProfile: null);

        Assert.That(cut.FindAll(Truncated), Is.Empty);
    }

    private IRenderedComponent<AwaitingSynchronisationNotice> RenderNotice(ObjectTypeAwaitingSynchronisation entry, ConnectedSystemRunProfile? runProfile) =>
        Render<AwaitingSynchronisationNotice>(p => p
            .Add(c => c.Entry, entry)
            .Add(c => c.RunProfile, runProfile));

    private static ConnectedSystemRunProfile RunProfile(ConnectedSystemRunType runType) => new() { Id = 1, Name = "Run", RunType = runType };

    private static ObjectTypeAwaitingSynchronisation Entry(int changeCount, int listed, bool activityIds = true) => new()
    {
        ConnectedSystemId = 3,
        ConnectedSystemObjectTypeId = 7,
        ObjectTypeName = "user",
        ChangeCount = changeCount,
        EarliestRequestedAt = DateTime.UtcNow.AddHours(-3),
        ObjectCount = 12_500,
        Changes = Enumerable.Range(1, listed).Select(i => new ConfigurationChangeSynchronisationSummary
        {
            Id = i,
            ConnectedSystemId = 3,
            ConnectedSystemObjectTypeId = 7,
            Reason = $"Change {i}",
            RequestedAt = DateTime.UtcNow.AddMinutes(-i),
            ConfigurationChangeActivityId = activityIds ? Guid.NewGuid() : null,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System"
        }).ToList()
    };
}
