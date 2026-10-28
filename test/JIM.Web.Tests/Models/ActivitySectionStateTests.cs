// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// Which of the Activity tab's sections are expanded (#2549), and how that choice is kept in the URL so a view can be
/// bookmarked. Everything is expanded by default, and the default keeps the URL clean.
/// </summary>
[TestFixture]
public class ActivitySectionStateTests
{
    [Test]
    public void Parse_NoValue_ShowsEverySection()
    {
        var state = ActivitySectionState.Parse(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Queued, Is.True);
            Assert.That(state.InProgress, Is.True);
            Assert.That(state.Completed, Is.True);
        }
    }

    [Test]
    public void Parse_ListedSections_ShowsOnlyThose()
    {
        var state = ActivitySectionState.Parse("queued,completed");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Queued, Is.True);
            Assert.That(state.InProgress, Is.False);
            Assert.That(state.Completed, Is.True);
        }
    }

    [Test]
    public void Parse_UnknownWordsAndCase_AreTolerated()
    {
        var state = ActivitySectionState.Parse(" Running , nonsense ");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Queued, Is.False);
            Assert.That(state.InProgress, Is.True);
            Assert.That(state.Completed, Is.False);
        }
    }

    [Test]
    public void Parse_Empty_CollapsesEverySection()
    {
        Assert.That(ActivitySectionState.Parse(""), Is.EqualTo(new ActivitySectionState(Queued: false, InProgress: false, Completed: false)));
    }

    [Test]
    public void ToQueryValue_EverySectionShown_IsNullSoTheUrlStaysClean()
    {
        Assert.That(ActivitySectionState.All.ToQueryValue(), Is.Null);
    }

    [Test]
    public void ToQueryValue_RoundTripsThroughParse()
    {
        var state = new ActivitySectionState(Queued: false, InProgress: true, Completed: true);

        Assert.That(ActivitySectionState.Parse(state.ToQueryValue()), Is.EqualTo(state));
    }

    [Test]
    public void ToQueryValue_NothingShown_IsEmptyRatherThanNull()
    {
        // Null would read back as "everything", which is the opposite of what was chosen.
        Assert.That(new ActivitySectionState(false, false, false).ToQueryValue(), Is.EqualTo(""));
    }

    [Test]
    public void Toggle_FlipsOnlyTheNamedSection()
    {
        var state = ActivitySectionState.All.Toggle(ActivitySection.InProgress);

        Assert.That(state, Is.EqualTo(new ActivitySectionState(true, false, true)));
    }
}
