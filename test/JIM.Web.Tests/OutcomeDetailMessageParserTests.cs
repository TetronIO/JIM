// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Parity tests for <see cref="OutcomeDetailMessageParser"/>, which extracts the overloaded
/// "csId|csoTypeName" channel previously parsed inline in OutcomeTreeNode.razor. The expected
/// behaviour mirrors the component exactly: the first pipe-delimited segment is treated as a
/// Connected System id when it parses as an integer; otherwise the whole message is plain text.
/// </summary>
[TestFixture]
public class OutcomeDetailMessageParserTests
{
    [Test]
    public void Parse_CsIdAndCsoTypeName_ReturnsBothParts()
    {
        var result = OutcomeDetailMessageParser.Parse("4|person");

        Assert.That(result.ConnectedSystemId, Is.EqualTo(4));
        Assert.That(result.CsoTypeName, Is.EqualTo("person"));
        Assert.That(result.PlainMessage, Is.Null);
    }

    [Test]
    public void Parse_PlainCsId_ReturnsIdWithoutTypeName()
    {
        var result = OutcomeDetailMessageParser.Parse("4");

        Assert.That(result.ConnectedSystemId, Is.EqualTo(4));
        Assert.That(result.CsoTypeName, Is.Null);
        Assert.That(result.PlainMessage, Is.Null);
    }

    [Test]
    public void Parse_CsIdWithEmptyTypeName_ReturnsNullTypeName()
    {
        // The worker writes "4|" when the CSO type name lookup misses; the tree treats the
        // empty segment as no type name at all.
        var result = OutcomeDetailMessageParser.Parse("4|");

        Assert.That(result.ConnectedSystemId, Is.EqualTo(4));
        Assert.That(result.CsoTypeName, Is.Null);
        Assert.That(result.PlainMessage, Is.Null);
    }

    [Test]
    public void Parse_NegativeCsId_ParsesAsIdForParityWithTree()
    {
        // int.TryParse accepts negative values, so the tree would have treated this as an id.
        var result = OutcomeDetailMessageParser.Parse("-1|person");

        Assert.That(result.ConnectedSystemId, Is.EqualTo(-1));
        Assert.That(result.CsoTypeName, Is.EqualTo("person"));
        Assert.That(result.PlainMessage, Is.Null);
    }

    [Test]
    public void Parse_NonNumericMessage_ReturnsPlainMessage()
    {
        const string message = "Deleted immediately: last authoritative source disconnected";
        var result = OutcomeDetailMessageParser.Parse(message);

        Assert.That(result.ConnectedSystemId, Is.Null);
        Assert.That(result.CsoTypeName, Is.Null);
        Assert.That(result.PlainMessage, Is.EqualTo(message));
    }

    [Test]
    public void Parse_NonNumericMessageContainingPipe_ReturnsWholeMessageAsPlainText()
    {
        const string message = "abc|def";
        var result = OutcomeDetailMessageParser.Parse(message);

        Assert.That(result.ConnectedSystemId, Is.Null);
        Assert.That(result.CsoTypeName, Is.Null);
        Assert.That(result.PlainMessage, Is.EqualTo(message));
    }

    [Test]
    public void Parse_Null_ReturnsAllNulls()
    {
        var result = OutcomeDetailMessageParser.Parse(null);

        Assert.That(result.ConnectedSystemId, Is.Null);
        Assert.That(result.CsoTypeName, Is.Null);
        Assert.That(result.PlainMessage, Is.Null);
    }

    [Test]
    public void Parse_EmptyString_ReturnsAllNulls()
    {
        var result = OutcomeDetailMessageParser.Parse(string.Empty);

        Assert.That(result.ConnectedSystemId, Is.Null);
        Assert.That(result.CsoTypeName, Is.Null);
        Assert.That(result.PlainMessage, Is.Null);
    }

    [Test]
    public void Parse_WhitespaceOnly_ReturnsWhitespaceAsPlainMessage()
    {
        // Whitespace does not parse as an integer, so parity with the tree means plain text.
        var result = OutcomeDetailMessageParser.Parse("   ");

        Assert.That(result.ConnectedSystemId, Is.Null);
        Assert.That(result.CsoTypeName, Is.Null);
        Assert.That(result.PlainMessage, Is.EqualTo("   "));
    }
}
