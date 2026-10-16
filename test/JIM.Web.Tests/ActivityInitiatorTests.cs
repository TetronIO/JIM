// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using Bunit;
using JIM.Models.Activities;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Who started an Activity, shown one way wherever it appears (#2066). System used to wear a clock on the Activity
/// pages, which read as "scheduled", although System also covers seeding and housekeeping; a Schedule's runs were
/// recorded as System under the name "Scheduler Service" and never named the Schedule.
/// </summary>
[TestFixture]
public class ActivityInitiatorTests : JimComponentTestContext
{
    private static readonly Guid ExecutionId = Guid.NewGuid();

    [Test]
    public void Render_ProducedByASchedule_NamesTheScheduleAndLinksItsExecution()
    {
        var cut = Render<ActivityInitiator>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.System)
            .Add(c => c.InitiatedByName, "Scheduler Service")
            .Add(c => c.ScheduleName, "Nightly HR sync")
            .Add(c => c.ScheduleExecutionId, ExecutionId));

        var schedule = cut.Find("[data-testid=jim-initiator-schedule]");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.TextContent, Does.Contain("Nightly HR sync"));
            Assert.That(cut.Find("[data-testid=jim-initiator-schedule] a").GetAttribute("href"),
                Is.EqualTo($"/admin/operations/schedule-executions/{ExecutionId}"));
            Assert.That(cut.FindAll("[data-testid=jim-initiator-system]"), Is.Empty);
        }
    }

    [Test]
    public void Render_ScheduleNameWithoutItsExecution_NamesItWithoutALink()
    {
        // The execution can be pruned long before the Activity it produced.
        var cut = Render<ActivityInitiator>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.System)
            .Add(c => c.ScheduleName, "Nightly HR sync"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid=jim-initiator-schedule]").TextContent, Does.Contain("Nightly HR sync"));
            Assert.That(cut.FindAll("[data-testid=jim-initiator-schedule] a"), Is.Empty);
        }
    }

    [Test]
    public void Render_SystemWithoutASchedule_IsSystem()
    {
        var cut = Render<ActivityInitiator>(p => p.Add(c => c.InitiatedByType, ActivityInitiatorType.System));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid=jim-initiator-system]").TextContent, Does.Contain("System"));
            Assert.That(cut.FindAll("[data-testid=jim-initiator-schedule]"), Is.Empty);
        }
    }

    [Test]
    public void Render_ApiKey_LinksTheKey()
    {
        var keyId = Guid.NewGuid();
        var cut = Render<ActivityInitiator>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.ApiKey)
            .Add(c => c.InitiatedById, keyId)
            .Add(c => c.InitiatedByName, "Integration key"));

        Assert.That(cut.Find("[data-testid=jim-initiator-api-key] a").GetAttribute("href"), Is.EqualTo($"/admin/apikeys/{keyId}"));
    }

    [Test]
    public void Render_UserWithAnObjectLink_LinksThePerson()
    {
        var cut = Render<ActivityInitiator>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.User)
            .Add(c => c.InitiatedByName, "Jay")
            .Add(c => c.UserHref, "/t/people/v/abc"));

        Assert.That(cut.Find("[data-testid=jim-initiator-user] a").GetAttribute("href"), Is.EqualTo("/t/people/v/abc"));
    }

    [Test]
    public void Render_UserWithoutAnObjectLink_NamesThePerson()
    {
        var cut = Render<ActivityInitiator>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.User)
            .Add(c => c.InitiatedByName, "Jay"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid=jim-initiator-user]").TextContent, Does.Contain("Jay"));
            Assert.That(cut.FindAll("[data-testid=jim-initiator-user] a"), Is.Empty);
        }
    }

    [Test]
    public void Render_Anonymous_IsNeverALink()
    {
        var cut = Render<ActivityInitiator>(p => p.Add(c => c.InitiatedByType, ActivityInitiatorType.Anonymous));

        Assert.That(cut.FindAll("[data-testid=jim-initiator-anonymous] a"), Is.Empty);
    }
}
