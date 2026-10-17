// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Filtering;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The one SCIM filter grammar (RFC 7644 section 3.4.2.2) the client builds its filters through and the service
/// provider parses them with. The two directions have to agree exactly: <c>Parse(Write(f)) == f</c> for any filter,
/// and a canonical filter string survives <c>Write(Parse(s)) == s</c>.
/// </summary>
public class ScimFilterGrammarTests
{
    private const string EnterpriseUrn = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    private static ScimAttributePath Path(string name, string? subAttribute = null, string? schemaUrn = null) =>
        new(schemaUrn, name, subAttribute);

    private static ScimComparisonFilter Eq(string name, string value) =>
        new(Path(name), ScimComparisonOperator.Eq, ScimFilterValue.String(value));

    #region parsing
    [Test]
    public void Parse_EqualityOnASimpleAttribute_IsOneComparison()
    {
        var filter = ScimFilterParser.Parse("userName eq \"bjensen\"");

        Assert.That(filter, Is.EqualTo(Eq("userName", "bjensen")));
    }

    [TestCase("eq", ScimComparisonOperator.Eq)]
    [TestCase("NE", ScimComparisonOperator.Ne)]
    [TestCase("Co", ScimComparisonOperator.Co)]
    [TestCase("sw", ScimComparisonOperator.Sw)]
    [TestCase("ew", ScimComparisonOperator.Ew)]
    [TestCase("gt", ScimComparisonOperator.Gt)]
    [TestCase("GE", ScimComparisonOperator.Ge)]
    [TestCase("lt", ScimComparisonOperator.Lt)]
    [TestCase("le", ScimComparisonOperator.Le)]
    public void Parse_ComparisonOperator_IsReadWhateverItsCase(string written, ScimComparisonOperator expected)
    {
        var filter = (ScimComparisonFilter)ScimFilterParser.Parse($"title {written} \"Tour Guide\"");

        Assert.That(filter.Operator, Is.EqualTo(expected));
    }

    [Test]
    public void Parse_Present_HasNoComparisonValue()
    {
        var filter = ScimFilterParser.Parse("title PR");

        Assert.That(filter, Is.EqualTo(new ScimComparisonFilter(Path("title"), ScimComparisonOperator.Pr, null)));
    }

    [Test]
    public void Parse_SubAttribute_IsDotted()
    {
        var filter = ScimFilterParser.Parse("meta.lastModified gt \"2011-05-13T04:42:34Z\"");

        Assert.That(filter, Is.EqualTo(new ScimComparisonFilter(
            Path("meta", "lastModified"), ScimComparisonOperator.Gt, ScimFilterValue.String("2011-05-13T04:42:34Z"))));
    }

    [Test]
    public void Parse_ExtensionAttribute_SeparatesTheSchemaUrnFromTheAttribute()
    {
        var filter = ScimFilterParser.Parse($"{EnterpriseUrn}:employeeNumber eq \"701984\"");

        Assert.That(filter, Is.EqualTo(new ScimComparisonFilter(
            Path("employeeNumber", schemaUrn: EnterpriseUrn), ScimComparisonOperator.Eq, ScimFilterValue.String("701984"))));
    }

    [Test]
    public void Parse_ExtensionSubAttribute_KeepsTheSubAttribute()
    {
        var filter = ScimFilterParser.Parse($"{EnterpriseUrn}:manager.value eq \"26118915\"");

        Assert.That(((ScimComparisonFilter)filter).Path, Is.EqualTo(Path("manager", "value", EnterpriseUrn)));
    }

    [Test]
    public void Parse_ReferenceSubAttribute_IsAValidName()
    {
        var filter = ScimFilterParser.Parse("members.$ref eq \"https://example.com/v2/Users/1\"");

        Assert.That(((ScimComparisonFilter)filter).Path, Is.EqualTo(Path("members", "$ref")));
    }

    [Test]
    public void Parse_AndBindsTighterThanOr()
    {
        var filter = ScimFilterParser.Parse("a eq \"1\" or b eq \"2\" and c eq \"3\"");

        Assert.That(filter, Is.EqualTo(new ScimLogicalFilter(ScimLogicalOperator.Or, Eq("a", "1"),
            new ScimLogicalFilter(ScimLogicalOperator.And, Eq("b", "2"), Eq("c", "3")))));
    }

    [Test]
    public void Parse_RepeatedOr_GroupsFromTheLeft()
    {
        var filter = ScimFilterParser.Parse("a eq \"1\" OR a eq \"2\" or a eq \"3\"");

        Assert.That(filter, Is.EqualTo(new ScimLogicalFilter(ScimLogicalOperator.Or,
            new ScimLogicalFilter(ScimLogicalOperator.Or, Eq("a", "1"), Eq("a", "2")), Eq("a", "3"))));
    }

    [Test]
    public void Parse_Parentheses_OverridePrecedence()
    {
        var filter = ScimFilterParser.Parse("(a eq \"1\" or b eq \"2\") and c eq \"3\"");

        Assert.That(filter, Is.EqualTo(new ScimLogicalFilter(ScimLogicalOperator.And,
            new ScimLogicalFilter(ScimLogicalOperator.Or, Eq("a", "1"), Eq("b", "2")), Eq("c", "3"))));
    }

    [Test]
    public void Parse_Not_NegatesItsParenthesisedFilter()
    {
        var filter = ScimFilterParser.Parse("userType eq \"Employee\" and not (emails co \"example.com\")");

        Assert.That(filter, Is.EqualTo(new ScimLogicalFilter(ScimLogicalOperator.And, Eq("userType", "Employee"),
            new ScimNotFilter(new ScimComparisonFilter(Path("emails"), ScimComparisonOperator.Co, ScimFilterValue.String("example.com"))))));
    }

    [Test]
    public void Parse_ValuePath_FiltersTheEntriesOfAMultiValuedAttribute()
    {
        var filter = ScimFilterParser.Parse("emails[type eq \"work\" and value co \"@example.com\"]");

        Assert.That(filter, Is.EqualTo(new ScimValuePathFilter(Path("emails"),
            new ScimLogicalFilter(ScimLogicalOperator.And, Eq("type", "work"),
                new ScimComparisonFilter(Path("value"), ScimComparisonOperator.Co, ScimFilterValue.String("@example.com"))))));
    }

    [Test]
    public void Parse_NonStringLiterals_KeepTheirKind()
    {
        var filter = ScimFilterParser.Parse("a eq true and b eq FALSE and c eq null and d gt -12.5e3");

        var comparisons = Flatten(filter).Select(c => c.Value).ToList();

        Assert.That(comparisons, Is.EqualTo(new[]
        {
            ScimFilterValue.Boolean(true), ScimFilterValue.Boolean(false), ScimFilterValue.Null, ScimFilterValue.Number("-12.5e3")
        }));
    }

    [Test]
    public void Parse_StringWithJsonEscapes_IsDecoded()
    {
        var filter = (ScimComparisonFilter)ScimFilterParser.Parse("userName eq \"o\\\"brien\\\\x\\u0041\\/\"");

        Assert.That(filter.Value, Is.EqualTo(ScimFilterValue.String("o\"brien\\xA/")));
    }

    [Test]
    public void Parse_ExtraWhitespace_IsIgnored()
    {
        var filter = ScimFilterParser.Parse("  ( userName   eq \"a\" )  ");

        Assert.That(filter, Is.EqualTo(Eq("userName", "a")));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("userName")]
    [TestCase("userName eq")]
    [TestCase("userName equals \"a\"")]
    [TestCase("userName 3 \"a\"")]
    [TestCase("userName eq bjensen")]
    [TestCase("userName eq \"a\" extra")]
    [TestCase("userName eq \"a\" and")]
    [TestCase("(userName eq \"a\"")]
    [TestCase("userName eq \"a\")")]
    [TestCase("not userName eq \"a\"")]
    [TestCase("emails[type eq \"work\"")]
    [TestCase("emails[]")]
    [TestCase("userName pr \"a\"")]
    [TestCase("1userName eq \"a\"")]
    [TestCase("user..name eq \"a\"")]
    [TestCase("userName eq \"a\\q\"")]
    [TestCase("userName eq \"a\\u00zz\"")]
    [TestCase("emails[type eq \"work\"].value eq \"a\"")]
    public void Parse_MalformedFilter_IsRefused(string filter)
    {
        Assert.That(() => ScimFilterParser.Parse(filter), Throws.TypeOf<ScimFilterException>());
    }

    [Test]
    public void Parse_UnterminatedString_DoesNotEchoTheValueInTheError()
    {
        // A filter can carry a value a client would not want written to a log, so a refusal names where the filter
        // went wrong, never what it held.
        var exception = Assert.Throws<ScimFilterException>(() => ScimFilterParser.Parse("userName eq \"S3cretValue"));

        Assert.That(exception!.Message, Does.Not.Contain("S3cretValue"));
    }

    [Test]
    public void Parse_DeeplyNestedParentheses_IsRefusedRatherThanExhaustingTheStack()
    {
        var filter = new string('(', 10_000) + "a eq \"1\"" + new string(')', 10_000);

        Assert.That(() => ScimFilterParser.Parse(filter), Throws.TypeOf<ScimFilterException>());
    }

    [Test]
    public void Parse_NestingWithinTheLimit_IsAccepted()
    {
        var filter = new string('(', ScimFilterParser.MaximumDepth - 1) + "a eq \"1\"" + new string(')', ScimFilterParser.MaximumDepth - 1);

        Assert.That(ScimFilterParser.Parse(filter), Is.EqualTo(Eq("a", "1")));
    }
    #endregion

    #region writing
    [TestCase("userName eq \"bjensen\"")]
    [TestCase("name.familyName co \"O'Malley\"")]
    [TestCase("title pr")]
    [TestCase("meta.lastModified gt \"2011-05-13T04:42:34Z\"")]
    [TestCase("title pr and userType eq \"Employee\"")]
    [TestCase("title pr or userType eq \"Intern\"")]
    [TestCase("userName eq \"a\" or userName eq \"b\" or userName eq \"c\"")]
    [TestCase("userType eq \"Employee\" and (emails co \"example.com\" or emails.value co \"example.org\")")]
    [TestCase("userType ne \"Employee\" and not (emails co \"example.com\" or emails.value co \"example.org\")")]
    [TestCase("userType eq \"Employee\" and emails[type eq \"work\" and value co \"@example.com\"]")]
    [TestCase("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:employeeNumber eq \"701984\"")]
    [TestCase("active eq true and loginCount ge 3 and nickName eq null")]
    [TestCase("(a eq \"1\" or b eq \"2\") and c eq \"3\"")]
    public void Write_ParsedCanonicalFilter_ReproducesIt(string filter)
    {
        Assert.That(ScimFilterWriter.Write(ScimFilterParser.Parse(filter)), Is.EqualTo(filter));
    }

    private static IEnumerable<TestCaseData> Trees()
    {
        yield return new TestCaseData(Eq("userName", "x\" or userName pr or userName eq \"y")).SetName("Write_Tree_ValueTryingToBecomeGrammar");
        yield return new TestCaseData(Eq("userName", "back\\slash\u0001control")).SetName("Write_Tree_BackslashAndControl");
        yield return new TestCaseData(new ScimLogicalFilter(ScimLogicalOperator.Or, Eq("a", "1"),
            new ScimLogicalFilter(ScimLogicalOperator.Or, Eq("b", "2"), Eq("c", "3")))).SetName("Write_Tree_RightNestedOr");
        yield return new TestCaseData(new ScimLogicalFilter(ScimLogicalOperator.And,
            new ScimLogicalFilter(ScimLogicalOperator.Or, Eq("a", "1"), Eq("b", "2")),
            new ScimLogicalFilter(ScimLogicalOperator.Or, Eq("c", "3"), Eq("d", "4")))).SetName("Write_Tree_OrUnderAnd");
        yield return new TestCaseData(new ScimNotFilter(new ScimNotFilter(Eq("a", "1")))).SetName("Write_Tree_DoubleNegation");
        yield return new TestCaseData(new ScimValuePathFilter(Path("emails"),
            new ScimLogicalFilter(ScimLogicalOperator.Or, Eq("type", "work"), new ScimNotFilter(Eq("type", "home"))))).SetName("Write_Tree_ValuePathWithOrAndNot");
        yield return new TestCaseData(new ScimComparisonFilter(Path("manager", "$ref", EnterpriseUrn), ScimComparisonOperator.Eq,
            ScimFilterValue.String("../Users/1"))).SetName("Write_Tree_ExtensionReference");
        yield return new TestCaseData(new ScimComparisonFilter(Path("loginCount"), ScimComparisonOperator.Le,
            ScimFilterValue.Number(-3.25m))).SetName("Write_Tree_NegativeDecimal");
    }

    [TestCaseSource(nameof(Trees))]
    public void Write_AnyTree_ParsesBackToTheSameTree(ScimFilter tree)
    {
        Assert.That(ScimFilterParser.Parse(ScimFilterWriter.Write(tree)), Is.EqualTo(tree));
    }

    [Test]
    public void Write_ValueContainingAQuote_EscapesItAsAJsonString()
    {
        Assert.That(ScimFilterWriter.Write(Eq("userName", "o\"brien")), Is.EqualTo("userName eq \"o\\\"brien\""));
    }

    [Test]
    public void Write_AnyOf_JoinsOneEqualityPerValueWithOr()
    {
        var filter = ScimFilter.AnyOf(Path("userName"), ["joe.bloggs", "joe.bloggs1"]);

        Assert.That(ScimFilterWriter.Write(filter), Is.EqualTo("userName eq \"joe.bloggs\" or userName eq \"joe.bloggs1\""));
    }

    [Test]
    public void AnyOf_NoValues_IsRefused()
    {
        Assert.That(() => ScimFilter.AnyOf(Path("userName"), []), Throws.ArgumentException);
    }

    [Test]
    public void Write_PresentWithAValue_IsRefused()
    {
        var filter = new ScimComparisonFilter(Path("title"), ScimComparisonOperator.Pr, ScimFilterValue.String("x"));

        Assert.That(() => ScimFilterWriter.Write(filter), Throws.ArgumentException);
    }

    [Test]
    public void Write_ComparisonWithoutAValue_IsRefused()
    {
        var filter = new ScimComparisonFilter(Path("title"), ScimComparisonOperator.Eq, null);

        Assert.That(() => ScimFilterWriter.Write(filter), Throws.ArgumentException);
    }

    [TestCase(null, "userName", null, "userName")]
    [TestCase(null, "meta", "lastModified", "meta.lastModified")]
    [TestCase(EnterpriseUrn, "manager", "value", EnterpriseUrn + ":manager.value")]
    public void Write_AttributePath_IsItsFilterForm(string? urn, string name, string? subAttribute, string expected)
    {
        Assert.That(ScimFilterWriter.Write(Path(name, subAttribute, urn)), Is.EqualTo(expected));
    }
    #endregion

    #region PATCH paths
    [Test]
    public void ParsePath_Attribute_IsAPathWithNoFilter()
    {
        var path = ScimFilterParser.ParsePath("name.givenName");

        Assert.That(path, Is.EqualTo(new ScimPatchPath(Path("name", "givenName"), null, null)));
    }

    [Test]
    public void ParsePath_ValueFilter_SelectsEntries()
    {
        var path = ScimFilterParser.ParsePath("members[value eq \"2819c223\"]");

        Assert.That(path, Is.EqualTo(new ScimPatchPath(Path("members"), Eq("value", "2819c223"), null)));
    }

    [Test]
    public void ParsePath_ValueFilterThenSubAttribute_SelectsOneSubAttributeOfTheEntries()
    {
        var path = ScimFilterParser.ParsePath("emails[type eq \"work\"].value");

        Assert.That(path, Is.EqualTo(new ScimPatchPath(Path("emails"), Eq("type", "work"), "value")));
    }

    [Test]
    public void ParsePath_UnquotedBooleanInAValueFilter_IsABoolean()
    {
        var path = ScimFilterParser.ParsePath("emails[primary eq true].value");

        Assert.That(path.ValueFilter, Is.EqualTo(new ScimComparisonFilter(Path("primary"), ScimComparisonOperator.Eq, ScimFilterValue.Boolean(true))));
    }

    [Test]
    public void ParsePath_ExtensionAttribute_KeepsItsUrn()
    {
        var path = ScimFilterParser.ParsePath($"{EnterpriseUrn}:manager");

        Assert.That(path, Is.EqualTo(new ScimPatchPath(Path("manager", schemaUrn: EnterpriseUrn), null, null)));
    }

    [TestCase("members[value eq \"2819c223\"]")]
    [TestCase("emails[type eq \"work\"].value")]
    [TestCase("addresses[type eq \"work\" and primary eq true].streetAddress")]
    [TestCase("name.familyName")]
    [TestCase("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department")]
    public void WritePath_ParsedPath_ReproducesIt(string path)
    {
        Assert.That(ScimFilterWriter.WritePath(ScimFilterParser.ParsePath(path)), Is.EqualTo(path));
    }

    [TestCase("")]
    [TestCase("members[value eq \"a\"")]
    [TestCase("members[value eq \"a\"].")]
    [TestCase("members[value eq \"a\"].value.extra")]
    [TestCase("name.givenName.extra")]
    [TestCase("userName eq \"a\"")]
    [TestCase("members[value eq \"a\"] or userName pr")]
    public void ParsePath_MalformedPath_IsRefused(string path)
    {
        Assert.That(() => ScimFilterParser.ParsePath(path), Throws.TypeOf<ScimFilterException>());
    }
    #endregion

    private static List<ScimComparisonFilter> Flatten(ScimFilter filter) => filter switch
    {
        ScimComparisonFilter comparison => [comparison],
        ScimLogicalFilter logical => [.. Flatten(logical.Left), .. Flatten(logical.Right)],
        ScimNotFilter not => Flatten(not.Inner),
        ScimValuePathFilter valuePath => Flatten(valuePath.Filter),
        _ => []
    };
}
