// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The duration the large-preview confirmation states (#1530). An estimate is a rough number, so it is stated to the
/// minute and never as "0 minutes", which would read as no time at all for a preview that does take some.
/// </summary>
[TestFixture]
public class PreviewDataSetSizeDialogTests
{
    [TestCase(35 * 60 + 20, "35 minutes", TestName = "Approximately_SecondsPastAMinute_RoundToTheNearestMinute")]
    [TestCase(89 * 60 + 40, "1 hour 30 minutes", TestName = "Approximately_OverAnHour_StatesHoursAndMinutes")]
    [TestCase(12, "1 minute", TestName = "Approximately_UnderAMinute_StatesOneMinute")]
    public void Approximately_StatesTheDurationToTheMinute(int seconds, string expected)
    {
        Assert.That(PreviewDataSetSizeDialog.Approximately(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
    }
}
