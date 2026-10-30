// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Staging.DTOs;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Connected System list's indicator gains a state for changes awaiting the next synchronisation (#2615), beside the
/// drift chip rather than inside it: those changes never count as drift, so a system can show either or both.
/// </summary>
[TestFixture]
public class ConfigurationDriftIndicatorAwaitingTests : JimComponentTestContext
{
    private const string AwaitingChip = "[data-testid='jim-awaiting-synchronisation-chip']";

    [Test]
    public void Render_NothingDriftedAndNothingAwaiting_ShowsNothing()
    {
        var cut = RenderIndicator(new ConfigurationDriftStatus { ConnectedSystemId = 1, LastFullSynchronisation = DateTime.UtcNow.AddDays(-1) });

        Assert.That(cut.FindComponents<MudChip<string>>(), Is.Empty);
    }

    [Test]
    public void Render_ChangesAwaitingOnly_ShowsTheAwaitingChipAlone()
    {
        var cut = RenderIndicator(new ConfigurationDriftStatus
        {
            ConnectedSystemId = 1,
            LastFullSynchronisation = DateTime.UtcNow.AddDays(-1),
            AwaitingSynchronisation = [Awaiting("user", 2), Awaiting("group", 3)]
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudChip<string>>(), Has.Count.EqualTo(1));
            Assert.That(cut.Find(AwaitingChip).TextContent.Trim(), Is.EqualTo("5"), "the saved changes waiting across both Object Types");
        }
    }

    [Test]
    public void Render_DriftOnly_ShowsTheDriftChipAlone()
    {
        var cut = RenderIndicator(new ConfigurationDriftStatus
        {
            ConnectedSystemId = 1,
            HasPendingChanges = true,
            ChangeCount = 4,
            LastFullSynchronisation = DateTime.UtcNow.AddDays(-1),
            HighestChangeClass = ConfigurationChangeClass.SyncAffecting
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudChip<string>>(), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(AwaitingChip), Is.Empty);
        }
    }

    [Test]
    public void Render_DriftAndChangesAwaiting_ShowsBothChips()
    {
        var cut = RenderIndicator(new ConfigurationDriftStatus
        {
            ConnectedSystemId = 1,
            HasPendingChanges = true,
            ChangeCount = 4,
            LastFullSynchronisation = DateTime.UtcNow.AddDays(-1),
            HighestChangeClass = ConfigurationChangeClass.SyncAffecting,
            AwaitingSynchronisation = [Awaiting("user", 1)]
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudChip<string>>(), Has.Count.EqualTo(2));
            Assert.That(cut.FindAll(AwaitingChip), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Render_ChangesAwaitingWhereDriftCannotBeDetermined_StillShowsTheAwaitingChip()
    {
        // The changes are real whether or not the question of drift has an answer.
        var cut = RenderIndicator(new ConfigurationDriftStatus
        {
            ConnectedSystemId = 1,
            TrackingDisabled = true,
            AwaitingSynchronisation = [Awaiting("user", 1)]
        });

        Assert.That(cut.FindAll(AwaitingChip), Has.Count.EqualTo(1));
    }

    private IRenderedComponent<ConfigurationDriftIndicator> RenderIndicator(ConfigurationDriftStatus status) =>
        Render<ConfigurationDriftIndicator>(p => p
            .Add(c => c.Status, status)
            .Add(c => c.ConnectedSystemName, "HR Import"));

    private static ObjectTypeAwaitingSynchronisation Awaiting(string objectTypeName, int changeCount) => new()
    {
        ConnectedSystemId = 1,
        ConnectedSystemObjectTypeId = objectTypeName.GetHashCode(),
        ObjectTypeName = objectTypeName,
        ChangeCount = changeCount,
        EarliestRequestedAt = DateTime.UtcNow.AddHours(-1)
    };
}
