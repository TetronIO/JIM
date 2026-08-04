// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="TooltipText"/>: the sentence-per-line split that keeps a two-sentence
/// explanation from running as one long line, and the encoding of the text it is handed.
/// </summary>
[TestFixture]
public class TooltipTextTests : JimComponentTestContext
{
    [Test]
    public void TooltipText_TwoSentences_BreaksBetweenThem()
    {
        var cut = Render<TooltipText>(p => p.Add(c => c.Text,
            "The object was detected as deleted from the source system. " +
            "It is now pending removal during the next synchronisation."));

        Assert.That(cut.FindAll("br"), Has.Count.EqualTo(1));
        Assert.That(cut.Markup, Does.StartWith("The object was detected as deleted from the source system.<br"));
        Assert.That(cut.Markup, Does.EndWith("It is now pending removal during the next synchronisation."));
    }

    [Test]
    public void TooltipText_ThreeSentences_BreaksAfterEachOne()
    {
        var cut = Render<TooltipText>(p => p.Add(c => c.Text, "One. Two. Three."));

        Assert.That(cut.FindAll("br"), Has.Count.EqualTo(2));
    }

    [Test]
    public void TooltipText_SingleSentence_RendersWithoutABreak()
    {
        // Not every description is two sentences, and a break is only ever a sentence boundary, so a
        // one-sentence description must render exactly as written. The site-wide tooltip measure is
        // what stops a long single sentence running off the page.
        var cut = Render<TooltipText>(p => p.Add(c => c.Text,
            "Attribute values were flowed from the Connected System Object to the Metaverse Object."));

        Assert.That(cut.FindAll("br"), Is.Empty);
    }

    [Test]
    public void TooltipText_SentenceEndingTheString_DoesNotEmitATrailingBreak()
    {
        var cut = Render<TooltipText>(p => p.Add(c => c.Text, "One. Two."));

        Assert.That(cut.FindAll("br"), Has.Count.EqualTo(1));
        Assert.That(cut.Markup, Does.Not.EndWith("<br />"));
    }

    [Test]
    public void TooltipText_MarkupInTheText_IsEncodedRatherThanRendered()
    {
        // The split renders each sentence through Blazor's encoder rather than as a MarkupString, so
        // a description that ever carries a value sourced from a connected system cannot inject markup.
        var cut = Render<TooltipText>(p => p.Add(c => c.Text, "<b>bold</b>. Second."));

        Assert.That(cut.FindAll("b"), Is.Empty);
        Assert.That(cut.Markup, Does.Contain("&lt;b&gt;bold&lt;/b&gt;"));
    }

    [Test]
    public void TooltipText_EmptyText_RendersNothing()
    {
        var cut = Render<TooltipText>(p => p.Add(c => c.Text, string.Empty));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }
}
