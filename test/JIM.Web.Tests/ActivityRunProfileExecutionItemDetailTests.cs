// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Web.Causality;
using JIM.Web.Pages;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Run Profile Execution Item detail page's own decisions. What its causality panel is given is read by
/// <see cref="CausalityItemLoader"/>, shared with the Causality Explorer, and is covered by
/// <see cref="CausalityItemLoaderTests"/>.
/// </summary>
[TestFixture]
public class ActivityRunProfileExecutionItemDetailTests
{
    [Test]
    public void ShowsCausality_ExportHeldForADecision_ShowsTheBandThoughNothingWasRecorded()
    {
        // A held export (#242, release 4) records no outcome, since nothing was exported; its band is what says why.
        var held = new ActivityRunProfileExecutionItem { ErrorType = ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved };
        var failed = new ActivityRunProfileExecutionItem { ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ActivityRunProfileExecutionItemDetail.ShowsCausality(held), Is.True);
            Assert.That(ActivityRunProfileExecutionItemDetail.ShowsCausality(failed), Is.False, "an item with nothing recorded and an ordinary error has no story to tell");
        }
    }
}
