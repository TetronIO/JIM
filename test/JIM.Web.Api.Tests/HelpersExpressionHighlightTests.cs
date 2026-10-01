// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HelpersExpressionHighlightTests
{
    [Test]
    public void HighlightExpression_NullInput_ReturnsEmptyString()
    {
        var result = Helpers.HighlightExpression(null!);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void HighlightExpression_EmptyInput_ReturnsEmptyString()
    {
        var result = Helpers.HighlightExpression(string.Empty);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void HighlightExpression_StringLiteral_WrapsInStringSpan()
    {
        var result = Helpers.HighlightExpression("\"hello\"");
        Assert.That(result, Does.Contain("jim-expr-string"));
        Assert.That(result, Does.Contain("hello"));
    }

    [Test]
    public void HighlightExpression_Number_WrapsInNumberSpan()
    {
        var result = Helpers.HighlightExpression("42");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-number\">42</span>"));
    }

    [Test]
    public void HighlightExpression_DecimalNumber_WrapsInNumberSpan()
    {
        var result = Helpers.HighlightExpression("3.14");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-number\">3.14</span>"));
    }

    [Test]
    public void HighlightExpression_TrueKeyword_WrapsInKeywordSpan()
    {
        var result = Helpers.HighlightExpression("true");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-keyword\">true</span>"));
    }

    [Test]
    public void HighlightExpression_FalseKeyword_WrapsInKeywordSpan()
    {
        var result = Helpers.HighlightExpression("false");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-keyword\">false</span>"));
    }

    [Test]
    public void HighlightExpression_NullKeyword_WrapsInKeywordSpan()
    {
        var result = Helpers.HighlightExpression("null");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-keyword\">null</span>"));
    }

    [Test]
    public void HighlightExpression_MvVariable_WrapsInVariableSpan()
    {
        var result = Helpers.HighlightExpression("mv");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-variable\">mv</span>"));
    }

    [Test]
    public void HighlightExpression_CsVariable_WrapsInVariableSpan()
    {
        var result = Helpers.HighlightExpression("cs");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-variable\">cs</span>"));
    }

    [Test]
    public void HighlightExpression_FunctionCall_WrapsInFunctionSpan()
    {
        var result = Helpers.HighlightExpression("Trim(x)");
        Assert.That(result, Does.Contain("<span class=\"jim-expr-function\">Trim</span>"));
    }

    [Test]
    public void HighlightExpression_BuiltInFunctions_WrapsInFunctionSpan()
    {
        var result = Helpers.HighlightExpression("Upper(x)");
        Assert.That(result, Does.Contain("<span class=\"jim-expr-function\">Upper</span>"));
    }

    [Test]
    public void HighlightExpression_UnknownFunctionCall_StillWrapsInFunctionSpan()
    {
        var result = Helpers.HighlightExpression("CustomFunc(x)");
        Assert.That(result, Does.Contain("<span class=\"jim-expr-function\">CustomFunc</span>"));
    }

    [Test]
    public void HighlightExpression_IdentifierWithoutParens_NoSpecialClass()
    {
        var result = Helpers.HighlightExpression("someVar");
        Assert.That(result, Is.EqualTo("someVar"));
    }

    [Test]
    public void HighlightExpression_EqualsOperator_WrapsInOperatorSpan()
    {
        var result = Helpers.HighlightExpression("==");
        Assert.That(result, Does.Contain("jim-expr-operator"));
    }

    [Test]
    public void HighlightExpression_NotEqualsOperator_WrapsInOperatorSpan()
    {
        var result = Helpers.HighlightExpression("!=");
        Assert.That(result, Does.Contain("jim-expr-operator"));
    }

    [Test]
    public void HighlightExpression_NullCoalescing_WrapsInOperatorSpan()
    {
        var result = Helpers.HighlightExpression("??");
        Assert.That(result, Does.Contain("jim-expr-operator"));
    }

    [Test]
    public void HighlightExpression_Punctuation_WrapsInPunctuationSpan()
    {
        var result = Helpers.HighlightExpression("(");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-punctuation\">(</span>"));
    }

    [Test]
    public void HighlightExpression_SquareBrackets_WrapsInPunctuationSpan()
    {
        var result = Helpers.HighlightExpression("[");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-punctuation\">[</span>"));
    }

    [Test]
    public void HighlightExpression_MixedExpression_HighlightsAllTokens()
    {
        var result = Helpers.HighlightExpression("IIF(mv[\"Active\"] == true, Upper(cs[\"Name\"]), null)");

        Assert.That(result, Does.Contain("<span class=\"jim-expr-function\">IIF</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-accessor-mv\">mv[&quot;Active&quot;]</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-operator\">==</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-keyword\">true</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-function\">Upper</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-accessor-cs\">cs[&quot;Name&quot;]</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-keyword\">null</span>"));
    }

    [Test]
    public void HighlightExpression_HtmlSpecialChars_AreEncoded()
    {
        var result = Helpers.HighlightExpression("\"<script>\"");
        Assert.That(result, Does.Not.Contain("<script>"));
        Assert.That(result, Does.Contain("&lt;script&gt;"));
    }

    [Test]
    public void HighlightExpression_EscapedQuoteInString_HandledCorrectly()
    {
        var result = Helpers.HighlightExpression("\"hello\\\"world\"");
        Assert.That(result, Does.Contain("jim-expr-string"));
        // Should be a single string span, not broken
        var spanCount = System.Text.RegularExpressions.Regex.Matches(result, "jim-expr-string").Count;
        Assert.That(spanCount, Is.EqualTo(1));
    }

    [Test]
    public void HighlightExpression_Whitespace_Preserved()
    {
        var result = Helpers.HighlightExpression("a + b");
        Assert.That(result, Does.Contain(" "));
    }

    // ─── Attribute accessors ───

    [Test]
    public void HighlightExpression_MetaverseAccessor_RendersAsOneMetaverseAccessorSpan()
    {
        var result = Helpers.HighlightExpression("mv[\"Account Name\"]");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-accessor-mv\">mv[&quot;Account Name&quot;]</span>"));
    }

    [Test]
    public void HighlightExpression_ConnectedSystemAccessor_RendersAsOneConnectedSystemAccessorSpan()
    {
        var result = Helpers.HighlightExpression("cs[\"firstName\"]");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-accessor-cs\">cs[&quot;firstName&quot;]</span>"));
    }

    [Test]
    public void HighlightExpression_AccessorWithWhitespace_RendersAsOneAccessorSpanPreservingWhitespace()
    {
        var result = Helpers.HighlightExpression("mv [ \"X\" ]");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-accessor-mv\">mv [ &quot;X&quot; ]</span>"));
    }

    [Test]
    public void HighlightExpression_AccessorInsideFunction_LeavesSurroundingTokensHighlighted()
    {
        var result = Helpers.HighlightExpression("Lower(cs[\"firstName\"]) + \".\"");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Does.StartWith("<span class=\"jim-expr-function\">Lower</span><span class=\"jim-expr-punctuation\">(</span>"));
            Assert.That(result, Does.Contain("<span class=\"jim-expr-accessor-cs\">cs[&quot;firstName&quot;]</span><span class=\"jim-expr-punctuation\">)</span>"));
            Assert.That(result, Does.Contain("<span class=\"jim-expr-string\">&quot;.&quot;</span>"));
        }
    }

    [Test]
    public void HighlightExpression_UnterminatedAccessorString_RendersAccessorSpanToEnd()
    {
        var result = Helpers.HighlightExpression("mv[\"Acc");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-accessor-mv\">mv[&quot;Acc</span>"));
    }

    [Test]
    public void HighlightExpression_AccessorMissingClosingBracket_RendersAccessorSpanUpToString()
    {
        var result = Helpers.HighlightExpression("cs[\"a\" + 1");
        Assert.That(result, Does.StartWith("<span class=\"jim-expr-accessor-cs\">cs[&quot;a&quot;</span>"));
    }

    [Test]
    public void HighlightExpression_OpenBracketOnly_KeepsBareVariableTreatment()
    {
        var result = Helpers.HighlightExpression("mv[");
        Assert.That(result, Is.EqualTo("<span class=\"jim-expr-variable\">mv</span><span class=\"jim-expr-punctuation\">[</span>"));
    }

    [Test]
    public void HighlightExpression_BareVariableNotFollowedByBracket_KeepsVariableSpan()
    {
        var result = Helpers.HighlightExpression("mv + cs");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Does.Contain("<span class=\"jim-expr-variable\">mv</span>"));
            Assert.That(result, Does.Contain("<span class=\"jim-expr-variable\">cs</span>"));
            Assert.That(result, Does.Not.Contain("jim-expr-accessor"));
        }
    }

    [Test]
    public void HighlightExpression_IdentifierEndingInMv_IsNotAnAccessor()
    {
        var result = Helpers.HighlightExpression("xmv[\"a\"]");
        Assert.That(result, Does.Not.Contain("jim-expr-accessor"));
    }

    [Test]
    public void HighlightExpression_AccessorWithMarkupInName_IsEncoded()
    {
        var result = Helpers.HighlightExpression("mv[\"<script>alert(1)</script>\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Does.Not.Contain("<script>"));
            Assert.That(result, Does.Contain("&lt;script&gt;alert(1)&lt;/script&gt;"));
            Assert.That(result, Does.StartWith("<span class=\"jim-expr-accessor-mv\">"));
        }
    }

    [Test]
    public void HighlightExpression_MarkupOutsideStrings_IsEncoded()
    {
        var result = Helpers.HighlightExpression("a <b> & 'c'");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Does.Not.Contain("<b>"));
            Assert.That(result, Does.Contain("&amp;"));
            Assert.That(result, Does.Contain("&#39;c&#39;"));
        }
    }

    // ─── Text-content round trip (the editor overlay depends on it) ───

    private static readonly string[] RoundTripCorpus =
    [
        "IIF(mv[\"Active\"] == true, Upper(cs[\"Name\"]), null)",
        "Lower(cs[\"firstName\"]) + \".\" +\nLower(cs[\"lastName\"])\n",
        "\n\n",
        "\tTrim(\tcs[\"a\"]\t)",
        "\"unterminated",
        "mv[\"Acc",
        "cs[",
        "mv [ \"spaced\" ] + cs  [\"x\"]",
        "\"escaped \\\" quote\" + \"trailing backslash\\",
        "\"Zoë Ångström\" + cs[\"名前\"] + \"emoji 👩‍💻\" + 👍 + Ünïcödé",
        "mv[\"<script>alert('x')</script>\"] && a < b || c > d",
        "-1 + 2.5 - -3 * x % 4 ?? 5",
        "a & b | c ^ d ~ e @ f # g $ h ; i : j { k } l = m",
        "   leading and trailing   ",
        "\r\nwindows\r\nline endings\r\n",
        " non-breaking space and ​zero width",
        "mv[\"a\"]cs[\"b\"]mv",
        "\\",
        "\"",
        "[\"orphan\"]"
    ];

    [TestCaseSource(nameof(RoundTripCorpus))]
    public void HighlightExpression_AnyInput_TextContentEqualsInput(string input)
    {
        var result = Helpers.HighlightExpression(input);

        var textContent = WebUtility.HtmlDecode(Regex.Replace(result, "<[^>]*>", string.Empty));

        Assert.That(textContent, Is.EqualTo(input),
            "The editor overlays the highlighted markup on the raw text, so the markup's text content must match it exactly.");
    }

    [TestCaseSource(nameof(RoundTripCorpus))]
    public void HighlightExpression_AnyInput_EmitsOnlyJimExpressionSpans(string input)
    {
        var result = Helpers.HighlightExpression(input);

        var tags = Regex.Matches(result, "<[^>]*>").Select(m => m.Value).ToList();

        Assert.That(tags, Has.All.Matches("^(<span class=\"jim-expr-[a-z-]+\">|</span>)$"),
            "Every tag in the output must be one of the highlighter's own spans; anything else is unencoded input.");
    }
}
