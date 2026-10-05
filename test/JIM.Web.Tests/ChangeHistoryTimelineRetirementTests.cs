// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Transactional;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A retired generated value is an event on the Metaverse Object's Changes timeline (Unique Value Generation, #242,
/// Phase 6, mockup section D). The register is read whole, while the change history pages in, so what is pinned here
/// is how the two interleave: in time order, only within the stretch of history loaded so far, and through the same
/// search box as every other event.
/// </summary>
[TestFixture]
public class ChangeHistoryTimelineRetirementTests : JimComponentTestContext
{
    private const string RetirementMarker = "jim-timeline-retirement";

    private static readonly DateTime ChangeTime = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ChangeHistoryTimeline.ChangeGroup BuildChange(DateTime when, string attributeName = "Department") => new()
    {
        ChangeType = ObjectChangeType.Updated,
        ChangeTime = when,
        ChangeInitiatorType = "User",
        ChangeInitiatorName = "Ada Lovelace",
        ChangeMechanismType = "User",
        AttributeChanges =
        [
            new ChangeHistoryTimeline.AttributeChange
            {
                AttributeName = attributeName,
                ChangeType = ValueChangeType.Add,
                Value = "Engineering",
                AttributeType = AttributeDataType.Text
            }
        ]
    };

    private static RetiredGeneratedValueHeader BuildRetirement(DateTime when, string value = "j.okafor", string attributeName = "Account Name") => new()
    {
        Id = 1,
        MetaverseAttributeId = 3,
        AttributeName = attributeName,
        Value = value,
        RetiredAt = when,
        Reason = RetiredGeneratedValueReason.Superseded,
        ActivityId = Guid.NewGuid(),
        ActivityTargetName = "HR CSV Delta Synchronisation"
    };

    [Test]
    public void ChangeHistoryTimeline_Retirement_IsShownAsAnEventCarryingTheRetiredValue()
    {
        var cut = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, [BuildChange(ChangeTime)])
            .Add(c => c.Retirements, [BuildRetirement(ChangeTime.AddDays(1))]));

        var events = cut.FindAll($"[data-testid='{RetirementMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].TextContent, Does.Contain("j.okafor"));
            Assert.That(events[0].TextContent, Does.Contain("Account Name"));
            Assert.That(events[0].TextContent, Does.Contain("HR CSV Delta Synchronisation"), "names the synchronisation that caused it");
        }
    }

    [Test]
    public void ChangeHistoryTimeline_RetirementsButNoChanges_ShowsTheRetirementsRatherThanSayingThereIsNoHistory()
    {
        var cut = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, [])
            .Add(c => c.Retirements, [BuildRetirement(ChangeTime)]));

        Assert.That(cut.FindAll($"[data-testid='{RetirementMarker}']"), Has.Count.EqualTo(1));
    }

    [Test]
    public void ChangeHistoryTimeline_Retirement_SitsInTimeOrderAmongTheChanges()
    {
        var cut = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, [BuildChange(ChangeTime.AddDays(2), "Newer"), BuildChange(ChangeTime, "Older")])
            .Add(c => c.Retirements, [BuildRetirement(ChangeTime.AddDays(1))]));

        var markup = cut.Markup;
        var newer = markup.IndexOf("Newer", StringComparison.Ordinal);
        var retired = markup.IndexOf("j.okafor", StringComparison.Ordinal);
        var older = markup.IndexOf("Older", StringComparison.Ordinal);

        Assert.That(newer < retired && retired < older, Is.True, "newest first, the retirement between the two changes");
    }

    [Test]
    public void ChangeHistoryTimeline_HistoryNotFullyLoaded_HoldsBackRetirementsOlderThanWhatIsLoaded()
    {
        // The older retirement belongs among changes not loaded yet; shown now it would sit at the foot of the list,
        // out of order, and appear to be the oldest thing that ever happened to the object.
        var cut = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, [BuildChange(ChangeTime)])
            .Add(c => c.Retirements, [BuildRetirement(ChangeTime.AddDays(1), "kept"), BuildRetirement(ChangeTime.AddDays(-1), "held.back")])
            .Add(c => c.HistoryComplete, false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("kept"));
            Assert.That(cut.Markup, Does.Not.Contain("held.back"));
        }
    }

    [Test]
    public void ChangeHistoryTimeline_HistoryFullyLoaded_ShowsEveryRetirement()
    {
        var cut = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, [BuildChange(ChangeTime)])
            .Add(c => c.Retirements, [BuildRetirement(ChangeTime.AddDays(-1), "oldest.value")]));

        Assert.That(cut.Markup, Does.Contain("oldest.value"));
    }

    [Test]
    public void ChangeHistoryTimeline_Searched_NarrowsRetirementsByAttributeName()
    {
        var cut = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, [BuildChange(ChangeTime)])
            .Add(c => c.Retirements, [BuildRetirement(ChangeTime.AddDays(1))]));

        var search = cut.FindComponent<SearchField>();
        cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("Department")).GetAwaiter().GetResult();

        cut.WaitForAssertion(() => Assert.That(cut.FindAll($"[data-testid='{RetirementMarker}']"), Is.Empty));
    }
}
