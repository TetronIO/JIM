// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HelpersJsonHighlightTests
{
    private static readonly string[] Corpus =
    [
        "{}",
        "{\"name\":\"Alice\",\"age\":42,\"active\":true,\"manager\":null,\"score\":-1.5e3}",
        "[1, 2, \"three\", false]",
        "{\n  \"nested\": {\n    \"list\": [\"a\", \"b\"]\n  }\n}",
        "{\"quote\":\"she said \\\"hi\\\"\"}",
        "{\"path\":\"C:\\\\temp\\\\\",\"next\":\"after a trailing backslash\"}",
        "{\"markup\":\"<script>alert('x')</script> & more\"}",
        "not json at all <b>",
        "{\"unterminated\":\"abc"
    ];

    private static string TextContent(string markup) => WebUtility.HtmlDecode(Regex.Replace(markup, "<[^>]+>", string.Empty));

    [TestCaseSource(nameof(Corpus))]
    public void HighlightJson_AnyInput_KeepsTheTextExactly(string json)
    {
        Assert.That(TextContent(Helpers.HighlightJson(json)), Is.EqualTo(json));
    }

    [Test]
    public void HighlightJson_KeysAndValues_TakeTheirOwnClasses()
    {
        var result = Helpers.HighlightJson("{\"name\":\"Alice\",\"age\":42,\"active\":true,\"manager\":null}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Does.Contain("<span class=\"jim-json-key\">&quot;name&quot;</span>"));
            Assert.That(result, Does.Contain("<span class=\"jim-json-string\">&quot;Alice&quot;</span>"));
            Assert.That(result, Does.Contain("<span class=\"jim-json-number\">42</span>"));
            Assert.That(result, Does.Contain("<span class=\"jim-json-boolean\">true</span>"));
            Assert.That(result, Does.Contain("<span class=\"jim-json-null\">null</span>"));
        }
    }

    [Test]
    public void HighlightJson_StringEndingInAnEscapedBackslash_EndsTheStringThere()
    {
        // "C:\\" ends at its fourth quote character's predecessor; a highlighter that only looks one character back
        // reads the closing quote as escaped and colours everything after it as part of the string.
        var result = Helpers.HighlightJson("{\"path\":\"C:\\\\\",\"next\":1}");

        Assert.That(result, Does.Contain("<span class=\"jim-json-key\">&quot;next&quot;</span>"));
    }

    [Test]
    public void HighlightJson_MarkupInsideAString_IsEncoded()
    {
        Assert.That(Helpers.HighlightJson("{\"x\":\"<b>\"}"), Does.Not.Contain("<b>"));
    }

    [Test]
    public void FormatJson_CompactJson_IsIndented()
    {
        Assert.That(Helpers.FormatJson("{\"a\":1,\"b\":[true]}"), Is.EqualTo("{\n  \"a\": 1,\n  \"b\": [\n    true\n  ]\n}"));
    }

    [Test]
    public void FormatJson_NotJson_IsReturnedUnchanged()
    {
        Assert.That(Helpers.FormatJson("not json"), Is.EqualTo("not json"));
    }
}
