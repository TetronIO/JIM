// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A Schedule Execution's status chip colour. Complete With Error (#1787) shares the Tertiary colour of an Activity's
/// Complete With Error, because the two sit side by side on the Activity page and name the same kind of outcome.
/// </summary>
[TestFixture]
public class HelpersScheduleExecutionColourTests
{
    [TestCase(ScheduleExecutionStatus.Queued, Color.Default)]
    [TestCase(ScheduleExecutionStatus.InProgress, Color.Primary)]
    [TestCase(ScheduleExecutionStatus.Complete, Color.Success)]
    [TestCase(ScheduleExecutionStatus.CompleteWithError, Color.Tertiary)]
    [TestCase(ScheduleExecutionStatus.Failed, Color.Error)]
    [TestCase(ScheduleExecutionStatus.Cancelled, Color.Warning)]
    [TestCase(ScheduleExecutionStatus.Paused, Color.Warning)]
    public void GetScheduleExecutionMudBlazorColorForStatus_EachStatus_HasItsColour(ScheduleExecutionStatus status, Color expected)
    {
        Assert.That(Helpers.GetScheduleExecutionMudBlazorColorForStatus(status), Is.EqualTo(expected));
    }

    [Test]
    public void GetScheduleExecutionMudBlazorColorForStatus_CompleteWithError_MatchesTheActivityStatusColour()
    {
        Assert.That(Helpers.GetScheduleExecutionMudBlazorColorForStatus(ScheduleExecutionStatus.CompleteWithError),
            Is.EqualTo(Helpers.GetActivityMudBlazorColorForStatus(ActivityStatus.CompleteWithError)));
    }
}
