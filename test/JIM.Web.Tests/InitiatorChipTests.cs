// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Who started an Activity, shown one way wherever it appears (#2066). System used to wear a clock on the Activity
/// pages, which read as "scheduled", although System also covers seeding and housekeeping; a Schedule's runs were
/// recorded as System under the name "Scheduler Service" and never named the Schedule.
/// </summary>
[TestFixture]
public class InitiatorChipTests : JimComponentTestContext
{
    private static readonly Guid ExecutionId = Guid.NewGuid();

    [Test]
    public void Render_ProducedByASchedule_NamesTheScheduleAndLinksItsExecution()
    {
        var cut = Render<InitiatorChip>(p => p
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
        var cut = Render<InitiatorChip>(p => p
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
        var cut = Render<InitiatorChip>(p => p.Add(c => c.InitiatedByType, ActivityInitiatorType.System));

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
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.ApiKey)
            .Add(c => c.InitiatedById, keyId)
            .Add(c => c.InitiatedByName, "Integration key"));

        Assert.That(cut.Find("[data-testid=jim-initiator-api-key] a").GetAttribute("href"), Is.EqualTo($"/admin/apikeys/{keyId}"));
    }

    [Test]
    public void Render_UserWithAnObjectLink_LinksThePerson()
    {
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.User)
            .Add(c => c.InitiatedByName, "Jay")
            .Add(c => c.UserHref, "/t/people/v/abc"));

        Assert.That(cut.Find("[data-testid=jim-initiator-user] a").GetAttribute("href"), Is.EqualTo("/t/people/v/abc"));
    }

    [Test]
    public void Render_UserWithoutAnObjectLink_NamesThePerson()
    {
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.User)
            .Add(c => c.InitiatedByName, "Jay"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid=jim-initiator-user]").TextContent, Does.Contain("Jay"));
            Assert.That(cut.FindAll("[data-testid=jim-initiator-user] a"), Is.Empty);
        }
    }

    [Test]
    public void Render_UserIdWithoutAnObjectLink_LinksThePersonUnderTheUserType()
    {
        // A list row names the person by id without looking them up; the built-in User type is where a person lives.
        var personId = Guid.NewGuid();
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.User)
            .Add(c => c.InitiatedById, personId)
            .Add(c => c.InitiatedByName, "Jay"));

        Assert.That(cut.Find("[data-testid=jim-initiator-user] a").GetAttribute("href"), Is.EqualTo($"/t/users/v/{personId}"));
    }

    [Test]
    public void Render_UserLookedUpAndNotFound_NamesThePersonWithoutALink()
    {
        // The caller looked the person up and found no Metaverse Object (deleted since, or a history row the #2066
        // repair could not attribute), so a link by id could only lead to a page that does not exist (#2151).
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.User)
            .Add(c => c.InitiatedById, Guid.NewGuid())
            .Add(c => c.InitiatedByName, "Jay")
            .Add(c => c.UserLookedUp, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid=jim-initiator-user]").TextContent, Does.Contain("Jay"));
            Assert.That(cut.FindAll("[data-testid=jim-initiator-user] a"), Is.Empty);
        }
    }

    [Test]
    public void Render_Anonymous_IsNeverALink()
    {
        var cut = Render<InitiatorChip>(p => p.Add(c => c.InitiatedByType, ActivityInitiatorType.Anonymous));

        Assert.That(cut.FindAll("[data-testid=jim-initiator-anonymous] a"), Is.Empty);
    }

    // ─── The chip each kind of initiator wears (#2460 follow-up) ───

    [TestCase(ActivityInitiatorType.System, ObjectChipKind.System)]
    [TestCase(ActivityInitiatorType.ApiKey, ObjectChipKind.ApiKey)]
    [TestCase(ActivityInitiatorType.User, ObjectChipKind.MetaverseObject)]
    public void Render_Principal_WearsItsOwnChipKind(ActivityInitiatorType type, ObjectChipKind expected)
    {
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, type)
            .Add(c => c.InitiatedById, Guid.NewGuid())
            .Add(c => c.InitiatedByName, "Someone"));

        Assert.That(cut.FindComponent<ObjectChip>().Instance.Kind, Is.EqualTo(expected));
    }

    [Test]
    public void Render_Schedule_WearsTheScheduleChip()
    {
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.System)
            .Add(c => c.ScheduleName, "Nightly HR sync"));

        Assert.That(cut.FindComponent<ObjectChip>().Instance.Kind, Is.EqualTo(ObjectChipKind.Schedule));
    }

    [Test]
    public void Render_System_IsNeverALink()
    {
        // System is not something in JIM with a page of its own.
        var cut = Render<InitiatorChip>(p => p.Add(c => c.InitiatedByType, ActivityInitiatorType.System));

        Assert.That(cut.FindAll("[data-testid=jim-initiator-system] a"), Is.Empty);
    }

    [Test]
    public void Render_SynchronisationRuleWithItsConnectedSystem_IsTheSystemAndRulePill()
    {
        // A synchronisation's change was made by its rule, whoever started the run.
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.ApiKey)
            .Add(c => c.InitiatedByName, "Infrastructure Key")
            .Add(c => c.SyncRuleId, 7)
            .Add(c => c.SyncRuleName, "HR CSV Import Users")
            .Add(c => c.ConnectedSystemId, 3)
            .Add(c => c.ConnectedSystemName, "HR CSV"));

        var pill = cut.FindComponent<SystemRuleChip>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pill.SyncRuleName, Is.EqualTo("HR CSV Import Users"));
            Assert.That(pill.ConnectedSystemName, Is.EqualTo("HR CSV"));
            Assert.That(pill.SyncRuleHref, Is.EqualTo("/admin/sync-rules/7"));
            Assert.That(pill.ConnectedSystemHref, Is.EqualTo("/admin/connected-systems/3"));
            Assert.That(cut.FindAll("[data-testid=jim-initiator-api-key]"), Is.Empty, "the rule, not the run's initiator, made the change");
        }
    }

    [Test]
    public void Render_SynchronisationRuleWithoutItsConnectedSystem_IsTheRuleChip()
    {
        // A deletion records the rule's name but no execution item, so the Connected System is not known.
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.SyncRuleId, 7)
            .Add(c => c.SyncRuleName, "HR CSV Import Users"));

        var chip = cut.FindComponent<ObjectChip>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Kind, Is.EqualTo(ObjectChipKind.SynchronisationRule));
            Assert.That(chip.Href, Is.EqualTo("/admin/sync-rules/7"));
        }
    }

    [Test]
    public void Render_NoInitiatorRecordedButSystemMadeIt_IsSystem()
    {
        // A change recorded without its initiator still says which mechanism made it.
        var cut = Render<InitiatorChip>(p => p
            .Add(c => c.InitiatedByType, ActivityInitiatorType.NotSet)
            .Add(c => c.ChangeInitiatorType, MetaverseObjectChangeInitiatorType.System));

        Assert.That(cut.FindAll("[data-testid=jim-initiator-system]"), Has.Count.EqualTo(1));
    }
}
