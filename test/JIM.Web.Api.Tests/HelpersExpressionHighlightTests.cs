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
        Assert.That(result, Does.Contain("<span class=\"jim-expr-variable\">mv</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-string\">&quot;Active&quot;</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-operator\">==</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-keyword\">true</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-function\">Upper</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-variable\">cs</span>"));
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
    public void HighlightExpression_IndexerAccess_HighlightsVariableAndString()
    {
        var result = Helpers.HighlightExpression("mv[\"DisplayName\"]");
        Assert.That(result, Does.Contain("<span class=\"jim-expr-variable\">mv</span>"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-punctuation\">[</span>"));
        Assert.That(result, Does.Contain("jim-expr-string"));
        Assert.That(result, Does.Contain("DisplayName"));
        Assert.That(result, Does.Contain("<span class=\"jim-expr-punctuation\">]</span>"));
    }

    [Test]
    public void HighlightExpression_Whitespace_Preserved()
    {
        var result = Helpers.HighlightExpression("a + b");
        Assert.That(result, Does.Contain(" "));
    }
}
