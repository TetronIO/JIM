// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// Comparison values written into a SCIM filter, which the uniqueness probe's searches and the PATCH builder's value
/// paths share. Whatever a value holds, it has to stay one value and never become filter grammar.
/// </summary>
public class ScimFilterLiteralTests
{
    [Test]
    public void Quote_QuoteAndBackslash_AreEscapedAsAJsonStringRequires()
    {
        Assert.That(ScimFilterLiteral.Quote("o\"brien\\x"), Is.EqualTo("\"o\\\"brien\\\\x\""));
    }

    [Test]
    public void Quote_ControlCharacter_IsEscapedAsAUnicodeSequence()
    {
        Assert.That(ScimFilterLiteral.Quote("a\nb\u0001"), Is.EqualTo("\"a\\u000ab\\u0001\""));
    }

    [Test]
    public void Quote_NonAsciiCharacters_AreLeftAsTheyAre()
    {
        Assert.That(ScimFilterLiteral.Quote("zoë.o'connor&co"), Is.EqualTo("\"zoë.o'connor&co\""));
    }

    [Test]
    public void For_TextHeldByANumericAttribute_IsQuotedRatherThanWrittenAsGrammar()
    {
        // Written bare, this would close the filter and add a clause of its own.
        Assert.That(ScimFilterLiteral.For("1] or [value pr", AttributeDataType.LongNumber), Is.EqualTo("\"1] or [value pr\""));
    }

    [Test]
    public void For_BinaryValue_IsTheBase64TextAsAString()
    {
        Assert.That(ScimFilterLiteral.For(new byte[] { 1, 2, 3 }, AttributeDataType.Binary), Is.EqualTo("\"AQID\""));
    }
}
