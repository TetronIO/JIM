// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// Small configuration lists (Connectors, API Keys, Predefined Searches and friends) are loaded whole and
/// windowed in memory rather than growing a database range read each; this helper is the one place that
/// slicing lives, so every such page honours the window contract identically, in particular that a total of
/// null means "not counted" and never zero.
/// </summary>
[TestFixture]
public class VirtualisedWindowExtensionsTests
{
    private static readonly List<string> Source = ["alpha", "bravo", "charlie", "delta", "echo"];

    private static VirtualisedWindowRequest Request(int start, int count, bool includeTotal) =>
        new(start, count, null, "Name", false, includeTotal);

    [Test]
    public void ToWindow_MiddleOfTheList_ReturnsJustThatSlice()
    {
        var window = Source.ToWindow(Request(1, 2, includeTotal: false));

        Assert.That(window.Items, Is.EqualTo(new[] { "bravo", "charlie" }));
    }

    [Test]
    public void ToWindow_TotalRequested_CountsTheWholeFilteredList()
    {
        var window = Source.ToWindow(Request(0, 2, includeTotal: true));

        Assert.That(window.TotalItems, Is.EqualTo(5));
    }

    [Test]
    public void ToWindow_TotalNotRequested_ReturnsNullNeverZero()
    {
        var window = Source.ToWindow(Request(0, 2, includeTotal: false));

        Assert.That(window.TotalItems, Is.Null);
    }

    [Test]
    public void ToWindow_WindowRunsPastTheEnd_ReturnsWhatRemains()
    {
        var window = Source.ToWindow(Request(3, 10, includeTotal: false));

        Assert.That(window.Items, Is.EqualTo(new[] { "delta", "echo" }));
    }

    [Test]
    public void ToWindow_EmptySourceWithTotal_ReturnsAGenuineZero()
    {
        var window = Enumerable.Empty<string>().ToWindow(Request(0, 10, includeTotal: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items, Is.Empty);
            Assert.That(window.TotalItems, Is.Zero);
        }
    }
}
