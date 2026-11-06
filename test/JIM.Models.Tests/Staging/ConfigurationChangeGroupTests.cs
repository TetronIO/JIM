// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Collapsing the configuration changes behind a Connected System Object Type into one row per thing changed: the
/// Synchronisation Rule (or the Object Type's Object Matching Rule), what was done to it, and when it was last changed.
/// </summary>
[TestFixture]
public class ConfigurationChangeGroupTests
{
    private static readonly DateTime Start = new(2026, 11, 5, 19, 50, 0, DateTimeKind.Utc);
    private long _nextId;

    [Test]
    public void Group_NoChanges_ReturnsNoGroups()
    {
        Assert.That(ConfigurationChangeGroup.Group([]), Is.Empty);
    }

    [Test]
    public void Group_ChangesToOneRule_CollapsesIntoOneGroup()
    {
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Import Users: created", syncRuleId: 1),
            Change("Import Users: Attribute Flow changed (Sync-affecting)", syncRuleId: 1),
            Change("Import Users: Attribute Flow changed (Sync-affecting)", syncRuleId: 1)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[0].Subject, Is.EqualTo("Import Users"));
            Assert.That(groups[0].SyncRuleId, Is.EqualTo(1));
            Assert.That(groups[0].Changes, Has.Count.EqualTo(3));
        }
    }

    [Test]
    public void Group_ChangesToTwoRules_OneGroupEachInOrderOfFirstAppearance()
    {
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Import Groups: created", syncRuleId: 2),
            Change("Import Users: created", syncRuleId: 1),
            Change("Import Groups: Scope changed (Sync-affecting)", syncRuleId: 2)
        ]);

        Assert.That(groups.Select(group => group.Subject), Is.EqualTo(new[] { "Import Groups", "Import Users" }));
    }

    [Test]
    public void Group_RuleRenamedBetweenChanges_StaysOneGroupNamedAsItWasLast()
    {
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Old Name: created", syncRuleId: 1, minutes: 0),
            Change("New Name: Attribute Flow changed (Sync-affecting)", syncRuleId: 1, minutes: 1)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[0].Subject, Is.EqualTo("New Name"));
        }
    }

    [Test]
    public void Group_ObjectMatchingRuleChange_GroupsByItsSubjectWithNoRule()
    {
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("jimPerson: Object Matching Rule created", syncRuleId: null),
            Change("Import Users: created", syncRuleId: 1)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups, Has.Count.EqualTo(2));
            Assert.That(groups[0].Subject, Is.EqualTo("jimPerson"));
            Assert.That(groups[0].SyncRuleId, Is.Null);
            Assert.That(groups[0].Summary, Is.EqualTo("Object Matching Rule created"));
        }
    }

    [Test]
    public void Group_DeletedRuleAndLiveRuleOfTheSameName_KeptApart()
    {
        // A deleted rule's changes have lost their rule id; they are not the live rule's history.
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Import Users: created", syncRuleId: null),
            Change("Import Users: created", syncRuleId: 1)
        ]);

        Assert.That(groups, Has.Count.EqualTo(2));
    }

    [Test]
    public void Summary_RepeatedActions_CountedInTheOrderTheyHappened()
    {
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Import Users: created", syncRuleId: 1, minutes: 0),
            Change("Import Users: Scope changed (Sync-affecting)", syncRuleId: 1, minutes: 1),
            Change("Import Users: Scope changed (Sync-affecting)", syncRuleId: 1, minutes: 2),
            Change("Import Users: Attribute Flow changed (Sync-affecting)", syncRuleId: 1, minutes: 3),
            Change("Import Users: Attribute Flow changed (Sync-affecting)", syncRuleId: 1, minutes: 4),
            Change("Import Users: Attribute Flow changed (Sync-affecting)", syncRuleId: 1, minutes: 5)
        ]);

        Assert.That(groups[0].Summary, Is.EqualTo(
            "Created, then Scope changed (Sync-affecting) 2 times, then Attribute Flow changed (Sync-affecting) 3 times"));
    }

    [Test]
    public void Summary_ChangesListedNewestFirst_StillReadsOldestFirst()
    {
        // The Connected System's notice lists the newest change first; the summary tells the story in order regardless.
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Import Users: Attribute Flow changed (Sync-affecting)", syncRuleId: 1, minutes: 2),
            Change("Import Users: created", syncRuleId: 1, minutes: 0)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].Summary, Is.EqualTo("Created, then Attribute Flow changed (Sync-affecting)"));
            Assert.That(groups[0].Changes.Select(change => change.RequestedAt), Is.Ordered.Ascending);
        }
    }

    [Test]
    public void LastMadeAt_IsTheNewestChange()
    {
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Import Users: created", syncRuleId: 1, minutes: 0),
            Change("Import Users: Scope changed (Sync-affecting)", syncRuleId: 1, minutes: 7),
            Change("Import Users: Scope changed (Sync-affecting)", syncRuleId: 1, minutes: 3)
        ]);

        Assert.That(groups[0].LastMadeAt, Is.EqualTo(Start.AddMinutes(7)));
    }

    [Test]
    public void Initiators_SameInitiatorEveryTime_ListedOnce()
    {
        var keyId = Guid.NewGuid();
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Import Users: created", syncRuleId: 1, initiatorId: keyId),
            Change("Import Users: Scope changed (Sync-affecting)", syncRuleId: 1, initiatorId: keyId)
        ]);

        Assert.That(groups[0].Initiators, Has.Count.EqualTo(1));
    }

    [Test]
    public void Initiators_TwoPeople_BothListedNewestFirst()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var groups = ConfigurationChangeGroup.Group(
        [
            Change("Import Users: created", syncRuleId: 1, minutes: 0, initiatorId: first),
            Change("Import Users: Scope changed (Sync-affecting)", syncRuleId: 1, minutes: 1, initiatorId: second)
        ]);

        Assert.That(groups[0].Initiators.Select(initiator => initiator.InitiatedById), Is.EqualTo(new Guid?[] { second, first }));
    }

    [Test]
    public void Group_ReasonWithoutASubject_UsesTheWholeReasonAsTheSubject()
    {
        var groups = ConfigurationChangeGroup.Group([Change("Something saved", syncRuleId: null)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].Subject, Is.EqualTo("Something saved"));
            Assert.That(groups[0].Summary, Is.Empty);
        }
    }

    [Test]
    public void Group_RuleNameContainingAColon_SplitsAtTheLastOne()
    {
        var groups = ConfigurationChangeGroup.Group([Change("HR: Import Users: created", syncRuleId: 1)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].Subject, Is.EqualTo("HR: Import Users"));
            Assert.That(groups[0].Summary, Is.EqualTo("Created"));
        }
    }

    private ConfigurationChangeSynchronisationSummary Change(string reason, int? syncRuleId, int minutes = 0, Guid? initiatorId = null) => new()
    {
        Id = ++_nextId,
        ConnectedSystemId = 3,
        ConnectedSystemObjectTypeId = 7,
        ObjectTypeName = "jimPerson",
        Reason = reason,
        SyncRuleId = syncRuleId,
        RequestedAt = Start.AddMinutes(minutes),
        ConfigurationChangeActivityId = Guid.NewGuid(),
        InitiatedByType = ActivityInitiatorType.ApiKey,
        InitiatedById = initiatorId ?? Guid.Empty,
        InitiatedByName = "Infrastructure Key"
    };
}
