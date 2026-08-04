// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The counting window a progress report describes. An export makes two passes, and the second one
/// covers a different, usually much smaller, amount of work; reporting the whole export's totals
/// throughout left the second pass looking finished from the moment it started.
/// </summary>
[TestFixture]
public class ExportProgressInfoTests
{
    [Test]
    public void CountingWindow_ReportDescribingTheWholeExport_UsesTheExportsOwnTotals()
    {
        var progress = new ExportProgressInfo { TotalExports = 100050, ProcessedExports = 100000 };

        Assert.That(progress.CountingWindow, Is.EqualTo((100050, 100000)));
    }

    [Test]
    public void CountingWindow_ReportFromAPassWithItsOwnWork_UsesThatPassesTotals()
    {
        var progress = new ExportProgressInfo
        {
            TotalExports = 100050,
            ProcessedExports = 100000,
            PassTotal = 50,
            PassProcessed = 20
        };

        Assert.That(progress.CountingWindow, Is.EqualTo((50, 20)));
    }

    [Test]
    public void CountingWindow_PassThatHasCompletedNothingYet_ReadsAsNoneOfItsWorkDone()
    {
        // The deferred pass re-resolves references before it writes anything, so a window of
        // "nothing done yet" is the truthful reading rather than the previous pass's totals.
        var progress = new ExportProgressInfo
        {
            TotalExports = 100050,
            ProcessedExports = 100000,
            PassTotal = 50,
            PassProcessed = 0
        };

        Assert.That(progress.CountingWindow, Is.EqualTo((50, 0)));
    }
}
