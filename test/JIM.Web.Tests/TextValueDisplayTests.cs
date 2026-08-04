// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the three-way dispatch in TextValueDisplay: no value, present-but-whitespace, and a real
/// value. The whitespace case is the one that matters most: rendering it raw looks identical to no
/// value, which misleads an administrator about what a Connected System actually imported.
/// </summary>
[TestFixture]
public class TextValueDisplayTests : JimComponentTestContext
{
    [Test]
    public void TextValueDisplay_NullValue_RendersEmptyValue()
    {
        var cut = Render<TextValueDisplay>(p => p.Add(c => c.Value, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<EmptyValue>(), Is.True);
            Assert.That(cut.HasComponent<WhitespaceValue>(), Is.False);
        }
    }

    [Test]
    public void TextValueDisplay_EmptyValue_RendersEmptyValue()
    {
        var cut = Render<TextValueDisplay>(p => p.Add(c => c.Value, string.Empty));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<EmptyValue>(), Is.True);
            Assert.That(cut.HasComponent<WhitespaceValue>(), Is.False);
        }
    }

    [Test]
    public void TextValueDisplay_WhitespaceOnlyValue_RendersWhitespaceValueNotEmptyValue()
    {
        var cut = Render<TextValueDisplay>(p => p.Add(c => c.Value, "   "));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<WhitespaceValue>(), Is.True);
            Assert.That(cut.HasComponent<EmptyValue>(), Is.False);
        }
    }

    [Test]
    public void TextValueDisplay_WhitespaceOnlyValue_PassesValueThroughToWhitespaceValue()
    {
        var cut = Render<TextValueDisplay>(p => p.Add(c => c.Value, "\t"));

        Assert.That(cut.FindComponent<WhitespaceValue>().Instance.Value, Is.EqualTo("\t"));
    }

    [Test]
    public void TextValueDisplay_RealValue_RendersValueWithoutPlaceholder()
    {
        var cut = Render<TextValueDisplay>(p => p.Add(c => c.Value, "Alice"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Alice"));
            Assert.That(cut.HasComponent<EmptyValue>(), Is.False);
            Assert.That(cut.HasComponent<WhitespaceValue>(), Is.False);
        }
    }

    [Test]
    public void TextValueDisplay_ValueWithSurroundingWhitespace_TreatedAsRealValue()
    {
        // Only whitespace-ONLY values get the affordance; a padded real value is a real value.
        var cut = Render<TextValueDisplay>(p => p.Add(c => c.Value, "  Alice  "));

        Assert.That(cut.HasComponent<WhitespaceValue>(), Is.False);
    }
}
