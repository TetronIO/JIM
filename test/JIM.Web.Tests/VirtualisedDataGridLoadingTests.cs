// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using JIM.Web.Models;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the grid's side of the delayed loading indicator: a window that is slow to arrive must say so, from the
/// grid, the search box and the rows it is about to replace, and must stop saying so the moment it arrives. The
/// threshold itself (and that a quick window shows nothing) is pinned by <see cref="DelayedLoadingIndicatorTests"/>;
/// here the windows are held open well past it and the test waits for the indicator's state rather than sleeping.
/// It waits with <c>WaitForState</c>, not <c>WaitForAssertion</c>: NUnit records a failed assertion against the test
/// even when bUnit catches it to retry, so a condition that is false at first would fail the test however it ends.
/// </summary>
[TestFixture]
public class VirtualisedDataGridLoadingTests : JimComponentTestContext
{
    private static readonly TimeSpan IndicatorTimeout = TimeSpan.FromSeconds(5);

    private readonly List<string> _rows = Enumerable.Range(1, 5).Select(i => $"Row {i}").ToList();
    private readonly Queue<TaskCompletionSource<VirtualisedWindow<string>>> _pendingWindows = new();
    private bool _holdWindows;
    private int _requestCount;

    private Task<VirtualisedWindow<string>> LoadWindowAsync(VirtualisedWindowRequest request, CancellationToken token)
    {
        _requestCount++;
        var matching = string.IsNullOrEmpty(request.SearchText)
            ? _rows
            : _rows.Where(r => r.Contains(request.SearchText, StringComparison.OrdinalIgnoreCase)).ToList();
        var window = new VirtualisedWindow<string>(
            matching.Skip(request.StartIndex).Take(request.Count).ToList(),
            request.IncludeTotalCount ? matching.Count : null);

        if (!_holdWindows)
            return Task.FromResult(window);

        var pending = new TaskCompletionSource<VirtualisedWindow<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => pending.TrySetCanceled(token));
        _pendingWindows.Enqueue(pending);
        return pending.Task.ContinueWith(_ => window, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void ReleaseHeldWindows()
    {
        while (_pendingWindows.TryDequeue(out var pending))
            pending.TrySetResult(null!);
    }

    private IRenderedComponent<VirtualisedDataGrid<string>> RenderGrid() =>
        Render<VirtualisedDataGrid<string>>(p => p
            .Add(c => c.ContainerId, "loading-test-grid")
            .Add(c => c.DefaultSortBy, "name")
            .Add(c => c.LoadWindow, LoadWindowAsync)
            .Add(c => c.Columns, (RenderFragment)(builder => { })));

    [Test]
    public void SlowFirstWindow_ShowsTheIndicatorUntilItArrives()
    {
        _holdWindows = true;
        var cut = RenderGrid();

        // WaitForState fails the test if the state never arrives within the timeout.
        cut.WaitForState(() => cut.Instance.IsLoadingIndicatorShown, IndicatorTimeout);

        ReleaseHeldWindows();

        cut.WaitForState(() => !cut.Instance.IsLoadingIndicatorShown, IndicatorTimeout);
        Assert.That(cut.Instance.TotalItems, Is.EqualTo(_rows.Count));
    }

    [Test]
    public void FirstWindowCancelledBySecond_StillShowsTheCount()
    {
        // MudDataGrid's own first load asks for a one-row window, then the virtualiser asks for a screenful and the
        // first is cancelled. MudDataGrid re-renders (and so draws the toolbar's count) only when that first load
        // ends, which a cancelled one does with no total. The window that does bring the total re-renders only the
        // rows, so the grid has to re-render itself when the total arrives, or the count never appears. Both windows
        // are released inside the indicator's threshold, so its own re-render cannot mask the fault.
        _holdWindows = true;
        var cut = RenderGrid();
        cut.WaitForState(() => _requestCount >= 2, IndicatorTimeout);

        ReleaseHeldWindows();

        cut.WaitForState(() => cut.Instance.TotalItems == _rows.Count, IndicatorTimeout);
        cut.WaitForState(() => cut.FindComponent<TableObjectCount>().Instance.Count == _rows.Count, IndicatorTimeout);
    }

    [Test]
    public void SlowSearch_ShowsTheIndicatorOnTheSearchBoxAndMarksTheOldRowsStaleUntilResultsArrive()
    {
        var cut = RenderGrid();
        cut.WaitForState(() => cut.Instance.TotalItems == _rows.Count, IndicatorTimeout);

        _holdWindows = true;
        var searchField = cut.FindComponent<SearchField>();
        _ = cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("Row 3"));

        cut.WaitForState(() => cut.Instance.IsLoadingIndicatorShown, IndicatorTimeout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Instance.IsLoadingIndicatorShown, Is.True);
            Assert.That(cut.FindComponent<SearchField>().Instance.Loading, Is.True);
            Assert.That(cut.Instance.ShowsStaleRows, Is.True, "The rows on screen belong to the previous search.");
        }

        ReleaseHeldWindows();

        cut.WaitForState(() => !cut.Instance.IsLoadingIndicatorShown, IndicatorTimeout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Instance.IsLoadingIndicatorShown, Is.False);
            Assert.That(cut.FindComponent<SearchField>().Instance.Loading, Is.False);
            Assert.That(cut.Instance.ShowsStaleRows, Is.False);
            Assert.That(cut.Instance.TotalItems, Is.EqualTo(1));
        }
    }
}
