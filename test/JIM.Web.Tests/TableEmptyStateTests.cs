// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The empty state rendered inside a table or data grid's no-rows fragment. Its contract: say why the list is
/// empty, offer the way out only when the caller has one, and render no dead affordances (no icon slot, hint or
/// button unless the caller supplied them).
/// </summary>
[TestFixture]
public class TableEmptyStateTests : JimComponentTestContext
{
    [Test]
    public void TableEmptyState_PrimaryTextOnly_RendersTheMessageAndNothingElse()
    {
        var cut = Render<TableEmptyState>(p => p.Add(c => c.PrimaryText, "No results"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("No results"));
            Assert.That(cut.HasComponent<MudIcon>(), Is.False);
            Assert.That(cut.HasComponent<MudButton>(), Is.False);
        }
    }

    [Test]
    public void TableEmptyState_WithIconAndSecondaryText_RendersBoth()
    {
        var cut = Render<TableEmptyState>(p => p
            .Add(c => c.Icon, Icons.Material.Filled.SearchOff)
            .Add(c => c.PrimaryText, "No Service Principals match \"tina\"")
            .Add(c => c.SecondaryText, "Try a different search term, or clear the search."));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<MudIcon>(), Is.True);
            Assert.That(cut.Markup, Does.Contain("Try a different search term, or clear the search."));
        }
    }

    [Test]
    public void TableEmptyState_WithAction_RendersTheButtonAndInvokesTheCallback()
    {
        var invoked = false;
        var cut = Render<TableEmptyState>(p => p
            .Add(c => c.PrimaryText, "No Service Principals match \"tina\"")
            .Add(c => c.ActionText, "Clear Search")
            .Add(c => c.OnAction, () => invoked = true));

        cut.Find("button").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Clear Search"));
            Assert.That(invoked, Is.True);
        }
    }

    [Test]
    public void TableEmptyState_NoActionText_RendersNoButton()
    {
        // A button with nothing behind it is a dead affordance; the action only exists when the caller names it.
        var cut = Render<TableEmptyState>(p => p.Add(c => c.PrimaryText, "No Service Principals yet"));

        Assert.That(cut.HasComponent<MudButton>(), Is.False);
    }
}
