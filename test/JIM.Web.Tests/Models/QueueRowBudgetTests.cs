// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// How many rows the Queued section shows (#2549): as many as fit the window while In progress stays in view without
/// scrolling, and never fewer than three, so a short window still shows what is about to run.
/// </summary>
[TestFixture]
public class QueueRowBudgetTests
{
    [Test]
    public void Calculate_RoomForSeveralRows_ReturnsHowManyFit()
    {
        // 1000 high, Queued's rows start 400 down, In progress needs 200: 400 left at 50 a row.
        var rows = QueueRowBudget.Calculate(viewportHeight: 1000, rowsTop: 400, inProgressReserve: 200, rowHeight: 50);

        Assert.That(rows, Is.EqualTo(8));
    }

    [Test]
    public void Calculate_PartRowLeftOver_RoundsDown()
    {
        var rows = QueueRowBudget.Calculate(viewportHeight: 1000, rowsTop: 400, inProgressReserve: 200, rowHeight: 60);

        Assert.That(rows, Is.EqualTo(6));
    }

    [Test]
    public void Calculate_ShortWindow_NeverGoesBelowTheFloor()
    {
        var rows = QueueRowBudget.Calculate(viewportHeight: 500, rowsTop: 450, inProgressReserve: 200, rowHeight: 50);

        Assert.That(rows, Is.EqualTo(QueueRowBudget.MinimumRows));
    }

    [Test]
    public void Calculate_UnmeasuredRowHeight_ReturnsTheProvisionalBudget()
    {
        var rows = QueueRowBudget.Calculate(viewportHeight: 1000, rowsTop: 400, inProgressReserve: 200, rowHeight: 0);

        Assert.That(rows, Is.EqualTo(QueueRowBudget.ProvisionalRows));
    }
}
