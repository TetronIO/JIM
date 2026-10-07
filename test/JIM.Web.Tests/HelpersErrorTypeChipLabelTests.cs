// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The error chip on an Activity's execution items and its error filter (Unique Value Generation, #242, release 4, Phase
/// 9): a generated value held for a decision reads as the decision it waits on, the label the rest of the portal uses,
/// rather than its enum name; every other error keeps its spelled-out enum name.
/// </summary>
[TestFixture]
public class HelpersErrorTypeChipLabelTests
{
    [Test]
    public void GetErrorTypeChipLabel_HeldForADecision_IsTheDecisionLabel() =>
        Assert.That(Helpers.GetErrorTypeChipLabel(ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved), Is.EqualTo("Needs a decision"));

    [Test]
    public void GetErrorTypeChipLabel_AnyOtherError_IsItsNameSpelledOut() =>
        Assert.That(Helpers.GetErrorTypeChipLabel(ActivityRunProfileExecutionItemErrorType.AmbiguousMatch), Is.EqualTo("Ambiguous Match"));
}
