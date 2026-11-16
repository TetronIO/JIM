// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="VirtualisedFilterHeader{TValue}"/>: a sortable column header in a virtualised grid
/// that also carries a filter for that column, so a single-column filter lives beside the data it narrows rather
/// than in the toolbar.
/// </summary>
[TestFixture]
public class VirtualisedFilterHeaderTests : JimComponentTestContext
{
    private enum Colour { Any, Red, Blue }

    private static readonly IReadOnlyList<VirtualisedFilterOption<Colour>> Options =
    [
        new(Colour.Any, "Any"),
        new(Colour.Red, "Red"),
        new(Colour.Blue, "Blue")
    ];

    [Test]
    public void VirtualisedFilterHeader_Unfiltered_FilterButtonNamesTheColumnOnly()
    {
        var cut = Render(Header(new FakeSortable(), Colour.Any, _ => { }));

        var button = cut.Find("button[aria-label]");

        Assert.That(button.GetAttribute("aria-label"), Is.EqualTo("Filter Paint"));
    }

    [Test]
    public void VirtualisedFilterHeader_Filtered_FilterButtonNamesTheActiveFilter()
    {
        var cut = Render(Header(new FakeSortable(), Colour.Blue, _ => { }));

        var button = cut.Find("button[aria-label]");

        Assert.That(button.GetAttribute("aria-label"), Is.EqualTo("Filter Paint (Blue)"));
    }

    [Test]
    public async Task VirtualisedFilterHeader_OptionChosen_RaisesValueChangedWithThatValueAsync()
    {
        Colour? chosen = null;
        var cut = Render(Header(new FakeSortable(), Colour.Any, value => chosen = value));

        await cut.Find("button[aria-label]").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.That(cut.FindComponents<MudMenuItem>(), Has.Count.EqualTo(Options.Count)));
        var red = cut.FindComponents<MudMenuItem>().Single(i => i.Markup.Contains("Red"));
        await cut.InvokeAsync(() => red.Instance.OnClick.InvokeAsync(new MouseEventArgs()));

        Assert.That(chosen, Is.EqualTo(Colour.Red));
    }

    [Test]
    public async Task VirtualisedFilterHeader_FilterButtonClicked_DoesNotSortTheColumnAsync()
    {
        var sortable = new FakeSortable();
        var cut = Render(Header(sortable, Colour.Any, _ => { }));

        await cut.Find("button[aria-label]").ClickAsync(new MouseEventArgs());

        Assert.That(sortable.ToggledNames, Is.Empty);
    }

    [Test]
    public async Task VirtualisedFilterHeader_TitleClicked_SortsByTheColumnNameAsync()
    {
        var sortable = new FakeSortable();
        var cut = Render(Header(sortable, Colour.Any, _ => { }));

        await cut.FindComponent<VirtualisedSortHeader>().Find("span").ClickAsync(new MouseEventArgs());

        Assert.That(sortable.ToggledNames, Is.EqualTo(new[] { "paint" }));
    }

    private RenderFragment Header(IVirtualisedSortable sortable, Colour value, Action<Colour> onChanged)
    {
        var callback = EventCallback.Factory.Create(this, onChanged);
        return builder =>
        {
            // MudMenu renders its items through MudPopoverProvider, which has to sit beside the header.
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<CascadingValue<IVirtualisedSortable>>(1);
            builder.AddComponentParameter(2, "Value", sortable);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<VirtualisedFilterHeader<Colour>>(0);
                inner.AddComponentParameter(1, nameof(VirtualisedFilterHeader<Colour>.Title), "Paint");
                inner.AddComponentParameter(2, nameof(VirtualisedFilterHeader<Colour>.Name), "paint");
                inner.AddComponentParameter(3, nameof(VirtualisedFilterHeader<Colour>.Options), Options);
                inner.AddComponentParameter(4, nameof(VirtualisedFilterHeader<Colour>.UnfilteredValue), Colour.Any);
                inner.AddComponentParameter(5, nameof(VirtualisedFilterHeader<Colour>.Value), value);
                inner.AddComponentParameter(6, nameof(VirtualisedFilterHeader<Colour>.ValueChanged), callback);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };
    }

    private sealed class FakeSortable : IVirtualisedSortable
    {
        public List<string> ToggledNames { get; } = [];

        public string SortBy => "name";

        public bool SortDescending => false;

        public Task ToggleSortAsync(string name)
        {
            ToggledNames.Add(name);
            return Task.CompletedTask;
        }
    }
}
