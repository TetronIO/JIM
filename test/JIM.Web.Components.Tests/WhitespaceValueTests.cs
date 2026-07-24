// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Covers the whitespace visualisation in WhitespaceValue's tooltip. The tooltip is the only place
/// an administrator can see what a whitespace-only value actually contains, so the character
/// substitutions and the count have to be right.
/// </summary>
[TestFixture]
public class WhitespaceValueTests : JimComponentTestContext
{
    private string? TooltipTextFor(string? value) =>
        Render<WhitespaceValue>(p => p.Add(c => c.Value, value))
            .FindComponent<MudTooltip>().Instance.Text;

    [Test]
    public void WhitespaceValue_SingleSpace_VisualisesAsMiddleDotWithSingularCount()
    {
        Assert.That(TooltipTextFor(" "), Is.EqualTo("Present, but whitespace only (1 character): ·"));
    }

    [Test]
    public void WhitespaceValue_MultipleSpaces_VisualisesEachWithPluralCount()
    {
        Assert.That(TooltipTextFor("   "), Is.EqualTo("Present, but whitespace only (3 characters): ···"));
    }

    [Test]
    public void WhitespaceValue_Tab_VisualisesAsArrow()
    {
        Assert.That(TooltipTextFor("\t"), Is.EqualTo("Present, but whitespace only (1 character): →"));
    }

    [Test]
    public void WhitespaceValue_WindowsNewLine_StripsCarriageReturnAndCountsBothCharacters()
    {
        // The carriage return is dropped from the visualisation but still counts towards the length,
        // so the count reflects what is stored rather than what is drawn.
        Assert.That(TooltipTextFor("\r\n"), Is.EqualTo("Present, but whitespace only (2 characters): ¶"));
    }

    [Test]
    public void WhitespaceValue_MixedWhitespace_VisualisesEachCharacterType()
    {
        Assert.That(TooltipTextFor(" \t\n"), Is.EqualTo("Present, but whitespace only (3 characters): ·→¶"));
    }

    [Test]
    public void WhitespaceValue_NullValue_FallsBackToNoVisibleCharactersMessage()
    {
        Assert.That(TooltipTextFor(null), Is.EqualTo("Present, but contains no visible characters."));
    }

    [Test]
    public void WhitespaceValue_EmptyValue_FallsBackToNoVisibleCharactersMessage()
    {
        Assert.That(TooltipTextFor(string.Empty), Is.EqualTo("Present, but contains no visible characters."));
    }

    [Test]
    public void WhitespaceValue_AlwaysRendersTheWhitespaceAffordance()
    {
        var cut = Render<WhitespaceValue>(p => p.Add(c => c.Value, " "));

        Assert.That(cut.Markup, Does.Contain("(whitespace)"));
    }
}
