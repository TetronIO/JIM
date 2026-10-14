// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using Bunit.Rendering;
using JIM.Application.Expressions;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Insert function menu beside Insert attribute (#2053, PRD Phase 2 requirement 9): every function in the
/// catalogue, grouped as the docs' function reference groups them, with its signature and description, and a filter.
/// Choosing one puts its name and opening parenthesis at the caret.
/// </summary>
[TestFixture]
public class InsertFunctionMenuTests : JimComponentTestContext
{
    private readonly List<string> _inserted = [];

    private RenderFragment Menu() => builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<InsertFunctionMenu>(1);
        builder.AddComponentParameter(2, nameof(InsertFunctionMenu.OnInsert), EventCallback.Factory.Create<string>(this, text => _inserted.Add(text)));
        builder.CloseComponent();
    };

    private async Task<IRenderedComponent<ContainerFragment>> OpenAsync()
    {
        var cut = Render(Menu());
        await cut.Find("[data-testid='jim-insert-function-button']").ClickAsync(new MouseEventArgs());
        cut.WaitForElement("[data-testid='jim-insert-function-menu'] [data-insert-item]");
        return cut;
    }

    private static IReadOnlyList<string> Names(IRenderedComponent<ContainerFragment> cut) =>
        cut.FindAll("[data-testid='jim-insert-function-name']").Select(e => e.TextContent.Trim()).ToList();

    private static IReadOnlyList<string> Groups(IRenderedComponent<ContainerFragment> cut) =>
        cut.FindAll("[data-testid='jim-insert-function-group']").Select(e => e.TextContent.Trim()).ToList();

    [Test]
    public async Task InsertFunctionMenu_Open_ListsEveryFunctionInTheCatalogueAsync()
    {
        var cut = await OpenAsync();

        Assert.That(Names(cut), Is.EquivalentTo(ExpressionFunctionCatalogue.All.Select(f => f.Signature)));
    }

    [Test]
    public async Task InsertFunctionMenu_Open_GroupsTheFunctionsAsTheFunctionReferenceDoesAsync()
    {
        var cut = await OpenAsync();

        var expected = ExpressionFunctionCatalogue.All.Select(f => f.Category).Distinct().Order()
            .Select(ExpressionFunctionCatalogue.CategoryName);
        Assert.That(Groups(cut), Is.EqualTo(expected));
    }

    [Test]
    public async Task InsertFunctionMenu_Filter_MatchesTheNameAsync()
    {
        var cut = await OpenAsync();

        var search = cut.FindComponent<SearchField>();
        await search.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("regex"));

        cut.WaitForAssertion(() => Assert.That(Names(cut), Is.EqualTo(new[]
        {
            "RegexReplace(value, pattern, replacement[, ignoreCase])",
            "RegexExtract(value, pattern[, group])"
        })));
        Assert.That(Groups(cut), Is.EqualTo(new[] { ExpressionFunctionCatalogue.CategoryName(JIM.Models.Expressions.ExpressionFunctionCategory.Pattern) }),
            "a group with nothing left in it is not shown");
    }

    [Test]
    public async Task InsertFunctionMenu_Filter_MatchingNothing_SaysSoAsync()
    {
        var cut = await OpenAsync();

        var search = cut.FindComponent<SearchField>();
        await search.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("zzz"));

        cut.WaitForAssertion(() => Assert.That(cut.Find("[data-testid='jim-insert-function-none']").TextContent, Does.Contain("zzz")));
    }

    [Test]
    public async Task InsertFunctionMenu_ChoosingAFunction_InsertsItsNameAndOpeningParenthesisAndClosesAsync()
    {
        var cut = await OpenAsync();

        await cut.FindAll("button[data-insert-item]").First(e => e.TextContent.Contains("IsMatch(")).ClickAsync(new MouseEventArgs());

        cut.WaitForState(() => cut.FindAll("[data-testid='jim-insert-function-menu']").Count == 0);
        Assert.That(_inserted, Is.EqualTo(new[] { "IsMatch(" }));
    }

    [Test]
    public async Task InsertFunctionMenu_Escape_ClosesWithoutInsertingAsync()
    {
        var cut = await OpenAsync();

        await cut.Find("[data-testid='jim-insert-function-menu']").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForState(() => cut.FindAll("[data-testid='jim-insert-function-menu']").Count == 0);
        Assert.That(_inserted, Is.Empty);
    }
}
