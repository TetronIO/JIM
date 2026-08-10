// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// The Metaverse Object list keeps its view state in the URL so a refresh or a shared link lands where the reader
/// left off. Everything the round trip has to get right is here rather than in the page, because a position without
/// the filter and sort that produced it points at a different object entirely.
/// </summary>
[TestFixture]
public class VirtualisedListUrlStateTests
{
    private const string DefaultSort = "Display Name";

    [Test]
    public void Read_EmptyQuery_ReturnsTheListsOwnDefaults()
    {
        var state = VirtualisedListUrlState.Read(string.Empty, DefaultSort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.SearchText, Is.Null);
            Assert.That(state.SortBy, Is.EqualTo(DefaultSort));
            Assert.That(state.SortDescending, Is.False);
            Assert.That(state.FirstVisibleRow, Is.Zero);
        }
    }

    [Test]
    public void Read_NullQuery_ReturnsTheListsOwnDefaults()
    {
        var state = VirtualisedListUrlState.Read(null, DefaultSort);

        Assert.That(state.SortBy, Is.EqualTo(DefaultSort));
    }

    [Test]
    public void Read_AllParametersPresent_ParsesEveryOne()
    {
        var state = VirtualisedListUrlState.Read("?q=smith&sort=Job+Title&desc=1&row=1240", DefaultSort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.SearchText, Is.EqualTo("smith"));
            Assert.That(state.SortBy, Is.EqualTo("Job Title"));
            Assert.That(state.SortDescending, Is.True);
            Assert.That(state.FirstVisibleRow, Is.EqualTo(1240));
        }
    }

    [Test]
    public void Read_RowIsNotANumber_FallsBackToTheTopOfTheList()
    {
        // A hand-edited or truncated link must land somewhere valid rather than throwing at the reader.
        var state = VirtualisedListUrlState.Read("?row=not-a-number", DefaultSort);

        Assert.That(state.FirstVisibleRow, Is.Zero);
    }

    [Test]
    public void Read_RowIsNegative_FallsBackToTheTopOfTheList()
    {
        var state = VirtualisedListUrlState.Read("?row=-40", DefaultSort);

        Assert.That(state.FirstVisibleRow, Is.Zero);
    }

    [Test]
    public void Read_BlankSearchText_IsTreatedAsNoSearchRatherThanAnEmptyOne()
    {
        var state = VirtualisedListUrlState.Read("?q=%20%20", DefaultSort);

        Assert.That(state.SearchText, Is.Null);
    }

    [Test]
    public void Write_EverythingAtItsDefault_EmitsNoParametersAtAll()
    {
        var state = new VirtualisedListUrlState { SortBy = DefaultSort };

        var query = state.Write(string.Empty, DefaultSort);

        // A list nobody has searched, sorted or scrolled should give a clean, shareable URL.
        Assert.That(query, Is.Empty);
    }

    [Test]
    public void Write_NonDefaultValues_EmitsOnlyTheParametersThatDifferFromTheDefault()
    {
        var state = new VirtualisedListUrlState
        {
            SearchText = "smith",
            SortBy = "Job Title",
            SortDescending = true,
            FirstVisibleRow = 1240
        };

        var query = state.Write(string.Empty, DefaultSort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query, Does.Contain("q=smith"));
            Assert.That(query, Does.Contain("desc=1"));
            Assert.That(query, Does.Contain("row=1240"));
            // The sort column travels URL-encoded; assert on the decoded round trip rather than the encoding.
            Assert.That(VirtualisedListUrlState.Read(query, DefaultSort).SortBy, Is.EqualTo("Job Title"));
        }
    }

    [Test]
    public void Write_SortMatchesTheDefault_OmitsTheSortParameter()
    {
        var state = new VirtualisedListUrlState { SortBy = DefaultSort, FirstVisibleRow = 20 };

        var query = state.Write(string.Empty, DefaultSort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query, Does.Not.Contain("sort="));
            Assert.That(query, Does.Contain("row=20"));
        }
    }

    [Test]
    public void Write_PreservesUnrelatedParameters()
    {
        // The attribute-presence deep link (?search=hasAttribute:Email) is owned by the page, not by this state,
        // and dropping it would silently widen the reader's filter.
        var state = new VirtualisedListUrlState { SortBy = DefaultSort, FirstVisibleRow = 60 };

        var query = state.Write("?search=hasAttribute%3aEmail", DefaultSort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query, Does.Contain("search=hasAttribute"));
            Assert.That(query, Does.Contain("row=60"));
        }
    }

    [Test]
    public void Write_ValueReturnedToItsDefault_RemovesTheParameterItHadWritten()
    {
        // Clearing the search box must clear it from the URL too, or a refresh reinstates a filter the reader removed.
        var state = new VirtualisedListUrlState { SortBy = DefaultSort };

        var query = state.Write("?q=smith&row=1240", DefaultSort);

        Assert.That(query, Is.Empty);
    }

    [Test]
    public void Write_WithAPrefix_PrefixesEveryParameterItOwns()
    {
        // Two virtualised lists on one page (the Deleted Objects tabs) share one URL, so each list's parameters
        // carry its prefix and neither can overwrite the other's state.
        var state = new VirtualisedListUrlState { SearchText = "smith", SortBy = DefaultSort, FirstVisibleRow = 40 };

        var query = state.Write(string.Empty, DefaultSort, "cso-");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query, Does.Contain("cso-q=smith"));
            Assert.That(query, Does.Contain("cso-row=40"));
            Assert.That(query, Does.Not.Contain("&q=").And.Not.StartWith("q="));
        }
    }

    [Test]
    public void Read_WithAPrefix_ReadsOnlyItsOwnParameters()
    {
        var state = VirtualisedListUrlState.Read("?q=other-lists-search&cso-q=smith&cso-row=40", DefaultSort, "cso-");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.SearchText, Is.EqualTo("smith"));
            Assert.That(state.FirstVisibleRow, Is.EqualTo(40));
        }
    }

    [Test]
    public void Write_WithAPrefix_LeavesAnotherPrefixesParametersUntouched()
    {
        var state = new VirtualisedListUrlState { SortBy = DefaultSort };

        // Returning this list's values to their defaults must not remove the sibling list's state.
        var query = state.Write("?mvo-q=jones&cso-q=smith", DefaultSort, "cso-");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query, Does.Contain("mvo-q=jones"));
            Assert.That(query, Does.Not.Contain("cso-q"));
        }
    }

    [Test]
    public void ReadWrite_RoundTripsEveryValue()
    {
        var original = new VirtualisedListUrlState
        {
            SearchText = "o'brien & sons",
            SortBy = "Department",
            SortDescending = true,
            FirstVisibleRow = 993
        };

        var round = VirtualisedListUrlState.Read(original.Write(string.Empty, DefaultSort), DefaultSort);

        Assert.That(round, Is.EqualTo(original));
    }
}
