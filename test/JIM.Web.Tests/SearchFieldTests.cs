// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers what SearchField adds over a bare MudTextField: the immediacy settings that make a list filter as the user
/// types (the defect behind issue #864), and the placeholder/label interplay, which is the one piece of behaviour a
/// call site can get surprised by.
/// </summary>
[TestFixture]
public class SearchFieldTests : JimComponentTestContext
{
    [Test]
    public void SearchField_ByDefault_CommitsAsTheUserTypesRatherThanOnBlur()
    {
        var field = Render<SearchField>().FindComponent<MudTextField<string>>().Instance;

        Assert.Multiple(() =>
        {
            Assert.That(field.Immediate, Is.True, "A blur-commit search box does not filter until focus leaves it.");
            Assert.That(field.DebounceInterval, Is.EqualTo(300));
        });
    }

    [Test]
    public void SearchField_WithDebounceInterval_PassesItToTheUnderlyingField()
    {
        var cut = Render<SearchField>(p => p.Add(c => c.DebounceInterval, 500));

        Assert.That(cut.FindComponent<MudTextField<string>>().Instance.DebounceInterval, Is.EqualTo(500));
    }

    [Test]
    public void SearchField_WithNeitherPlaceholderNorLabel_FallsBackToASearchPlaceholder()
    {
        var cut = Render<SearchField>();

        Assert.That(cut.FindComponent<MudTextField<string>>().Instance.Placeholder, Is.EqualTo("Search"));
    }

    [Test]
    public void SearchField_WithALabel_OmitsTheFallbackPlaceholder()
    {
        var cut = Render<SearchField>(p => p.Add(c => c.Label, "Search object types"));

        var field = cut.FindComponent<MudTextField<string>>().Instance;

        Assert.Multiple(() =>
        {
            Assert.That(field.Label, Is.EqualTo("Search object types"));
            Assert.That(field.Placeholder, Is.Null, "A label and a duplicate placeholder would read as the same text twice.");
        });
    }

    [Test]
    public void SearchField_WithAnExplicitPlaceholder_KeepsIt()
    {
        var cut = Render<SearchField>(p => p.Add(c => c.Placeholder, "Search target or initiator"));

        Assert.That(
            cut.FindComponent<MudTextField<string>>().Instance.Placeholder,
            Is.EqualTo("Search target or initiator"));
    }

    [Test]
    public async Task SearchField_WhenTheTextChanges_RaisesValueChangedWithTheNewText()
    {
        string? observed = null;
        var cut = Render<SearchField>(p => p.Add(c => c.ValueChanged, text => observed = text));
        var inner = cut.FindComponent<MudTextField<string>>();

        await cut.InvokeAsync(() => inner.Instance.ValueChanged.InvokeAsync("ldap"));

        Assert.Multiple(() =>
        {
            Assert.That(observed, Is.EqualTo("ldap"));
            Assert.That(cut.Instance.Value, Is.EqualTo("ldap"), "The component should hold the text it just reported.");
        });
    }
}
