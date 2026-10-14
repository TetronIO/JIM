// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Models.Expressions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The expression library's one text form and one date-reading rule (#2153's N1, N2 and N3), and ParseDate: what a
/// date or number becomes as text, and what text is read as a date, are the same on every server whatever its
/// culture, and a date written by JIM reads back as the same instant.
/// </summary>
[TestFixture]
public class DynamicExpressoEvaluatorDateAndTextTests
{
    private static readonly DateTime March15 = new(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);

    private DynamicExpressoEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp() => _evaluator = new DynamicExpressoEvaluator();

    private object? Evaluate(string expression, Dictionary<string, object?>? mv = null, Dictionary<string, object?>? cs = null) =>
        _evaluator.Evaluate(expression, new ExpressionContext(mv ?? new Dictionary<string, object?>(), cs));

    private static T Under<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    #region N1: one text form

    [TestCase("en-GB")]
    [TestCase("en-US")]
    [TestCase("de-DE")]
    public void Evaluate_DateConcatenatedWithText_WritesJimsDateTextForm(string culture)
    {
        var result = Under(culture, () => Evaluate("\"Changed \" + mv[\"When\"]", new Dictionary<string, object?> { ["When"] = March15 }));

        Assert.That(result, Is.EqualTo("Changed 2026-03-15 10:30:00Z"));
    }

    [TestCase("en-GB")]
    [TestCase("de-DE")]
    public void Evaluate_DecimalConcatenatedWithText_UsesAFullStopAsTheDecimalPoint(string culture)
    {
        var result = Under(culture, () => Evaluate("\"Rate \" + mv[\"Rate\"]", new Dictionary<string, object?> { ["Rate"] = 1.5m }));

        Assert.That(result, Is.EqualTo("Rate 1.5"));
    }

    [Test]
    public void Evaluate_DateWithAFraction_WritesTheFractionToTheMicrosecond()
    {
        var when = March15.AddTicks(1_234_567);

        var result = Evaluate("ToString(mv[\"When\"])", new Dictionary<string, object?> { ["When"] = when });

        Assert.That(result, Is.EqualTo("2026-03-15 10:30:00.123456Z"));
    }

    [Test]
    public void Evaluate_DateTextForm_ReadsBackAsTheSameInstant()
    {
        var when = March15.AddTicks(1_234_560);

        var result = Evaluate("ParseDate(ToString(mv[\"When\"]), \"yyyy-MM-dd HH:mm:ss.FFFFFFK\")",
            new Dictionary<string, object?> { ["When"] = when });

        Assert.That(result, Is.EqualTo(when));
    }

    [Test]
    public void Evaluate_BinaryUsedAsText_IsAnExpressionError()
    {
        Assert.That(() => Evaluate("Upper(mv[\"Sid\"])", new Dictionary<string, object?> { ["Sid"] = new byte[] { 1, 2, 3 } }),
            Throws.TypeOf<ExpressionEvaluationException>(),
            "a Binary value has no text form, so using one as text must fail rather than write System.Byte[]");
    }

    [TestCase("IsNullOrEmpty")]
    [TestCase("IsNullOrWhitespace")]
    public void Evaluate_PresenceTestOfABinaryValue_IsAValueNotAnError(string function)
    {
        // The presence tests ask whether there is a value, not what its text is: a Binary attribute holding one has a
        // value, exactly as it did before Binary as text became an error (proposal M10: a value is a value).
        var result = Evaluate($"{function}(cs[\"objectSid\"])", cs: new Dictionary<string, object?> { ["objectSid"] = new byte[] { 1, 5, 0 } });

        Assert.That(result, Is.EqualTo(false));
    }

    [TestCase("IsNullOrEmpty")]
    [TestCase("IsNullOrWhitespace")]
    public void Evaluate_PresenceTestOfAnEmptyBinaryValue_IsNoValue(string function)
    {
        var result = Evaluate($"{function}(cs[\"objectSid\"])", cs: new Dictionary<string, object?> { ["objectSid"] = Array.Empty<byte>() });

        Assert.That(result, Is.EqualTo(true));
    }

    [TestCase("IsNullOrEmpty")]
    [TestCase("IsNullOrWhitespace")]
    public void Evaluate_PresenceTestOfAMissingBinaryValue_IsNoValue(string function)
    {
        Assert.That(Evaluate($"{function}(cs[\"objectSid\"])", cs: new Dictionary<string, object?>()), Is.EqualTo(true));
    }

    [Test]
    public void Evaluate_LocalDateConcatenatedWithText_KeepsTheZoneTheDateCarries()
    {
        // JIM reads dates from PostgreSQL with Npgsql's legacy timestamp behaviour, so a stored date reaches an
        // expression as Kind Local. Joining it to text with + writes it in the date's own zone, with its offset rather
        // than Z, until expression inputs are canonicalised as UTC (#2153 N4, slice S1); this pins today's form so that
        // change shows up as deliberate.
        var local = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Local);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var expectedZone = $"{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";

        var result = Evaluate("\"Changed \" + mv[\"When\"]", new Dictionary<string, object?> { ["When"] = local });

        Assert.That(result, Is.EqualTo($"Changed 2026-03-15 10:30:00{expectedZone}"));
    }

    [Test]
    public void Evaluate_LocalDateAsText_IsWrittenInUtc()
    {
        // ToString() goes through JIM's one text form, which writes the canonical UTC instant whatever the date's kind.
        var local = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Local);

        var result = Evaluate("ToString(mv[\"When\"])", new Dictionary<string, object?> { ["When"] = local });

        Assert.That(result, Is.EqualTo(local.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z"));
    }

    [Test]
    public void Evaluate_AnyExpression_RestoresTheHostCulture()
    {
        var after = Under("en-GB", () =>
        {
            Evaluate("\"x\" + mv[\"When\"]", new Dictionary<string, object?> { ["When"] = March15 });
            return CultureInfo.CurrentCulture.Name;
        });

        Assert.That(after, Is.EqualTo("en-GB"));
    }

    [Test]
    public void Evaluate_ExpressionFails_RestoresTheHostCulture()
    {
        var after = Under("en-GB", () =>
        {
            Assert.That(() => Evaluate("1 / ToInt(\"0\")"), Throws.Exception);
            return CultureInfo.CurrentCulture.Name;
        });

        Assert.That(after, Is.EqualTo("en-GB"));
    }

    #endregion

    #region N5: one expression error

    [TestCase("mv[\"Missing\"].ToString()", typeof(NullReferenceException))]
    [TestCase("Split(\"a,b\", \",\")[5]", typeof(IndexOutOfRangeException))]
    [TestCase("1 / ToInt(\"0\")", typeof(DivideByZeroException))]
    [TestCase("Upper(", typeof(DynamicExpresso.Exceptions.ParseException))]
    public void Evaluate_ExpressionFails_ThrowsTheOneExpressionErrorWrappingTheCause(string expression, Type cause)
    {
        // A null reference or an index out of range is the administrator's expression failing, exactly as a syntax
        // error is, so every consumer reports it as an expression error rather than as an unhandled error.
        var error = Assert.Throws<ExpressionEvaluationException>(() => Evaluate(expression));

        Assert.That(error!.InnerException, Is.InstanceOf(cause));
    }

    [Test]
    public void Test_ExpressionFailsWithANullReference_ReportsAFailureRatherThanThrowing()
    {
        var result = _evaluator.Test("mv[\"Missing\"].ToString()", new ExpressionContext());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorMessage, Is.Not.Empty);
        }
    }

    [Test]
    public void Test_ExpressionRaisesAnExceptionOutsideTheExpressionErrors_ReportsAFailureRatherThanThrowing()
    {
        // PadLeft past the maximum string length throws OutOfMemoryException without allocating anything. Evaluate lets
        // such a failure propagate, but Test carries every failure in its result: the portal tester, the REST endpoint,
        // Example Data and the generated-value previews all rely on it never throwing.
        var result = _evaluator.Test("\"x\".PadLeft(2000000000)", new ExpressionContext());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorMessage, Is.Not.Empty);
        }
    }

    #endregion

    #region N2: one date-reading rule

    [TestCase("en-GB")]
    [TestCase("en-US")]
    public void Evaluate_EqDateWithIsoText_ComparesTheInstant(string culture)
    {
        var result = Under(culture, () => Evaluate("Eq(mv[\"When\"], \"2026-03-15T11:30:00+01:00\")",
            new Dictionary<string, object?> { ["When"] = March15 }));

        Assert.That(result, Is.EqualTo(true));
    }

    [TestCase("en-GB", "15/03/2026 10:30:00")]
    [TestCase("en-US", "3/15/2026 10:30:00 AM")]
    public void Evaluate_EqDateWithTheHostsOwnDateText_IsNotEqual(string culture, string hostText)
    {
        // Each host's own text for the date used to compare equal on that host only; numeric dates are never read.
        var result = Under(culture, () => Evaluate($"Eq(mv[\"When\"], \"{hostText}\")",
            new Dictionary<string, object?> { ["When"] = March15 }));

        Assert.That(result, Is.EqualTo(false));
    }

    [Test]
    public void Evaluate_ToFileTimeOfANumericDate_GivesNoValue()
    {
        Assert.That(Evaluate("ToFileTime(\"03/15/2026\")"), Is.Null, "ToFileTime reads text only in ISO 8601");
    }

    [Test]
    public void Evaluate_ToFileTimeOfIsoText_GivesTheFileTimeOfThatInstant()
    {
        Assert.That(Evaluate("ToFileTime(\"2026-03-15T11:30:00+01:00\")"), Is.EqualTo(March15.ToFileTimeUtc()));
    }

    #endregion

    #region N3: FormatDate accepts a date or ISO text

    [Test]
    public void Evaluate_FormatDateOfAnAttributeWithoutACast_FormatsIt()
    {
        // The function reference's own example, which used to fail to parse without a (DateTime) cast.
        var result = Evaluate("FormatDate(mv[\"HireDate\"], \"yyyy-MM-dd\")", new Dictionary<string, object?> { ["HireDate"] = March15 });

        Assert.That(result, Is.EqualTo("2026-03-15"));
    }

    [Test]
    public void Evaluate_FormatDateOfIsoText_FormatsTheUtcInstant()
    {
        Assert.That(Evaluate("FormatDate(\"2026-03-15T11:30:00+01:00\", \"yyyy-MM-dd HH:mm\")"), Is.EqualTo("2026-03-15 10:30"));
    }

    [TestCase("FormatDate(\"15/03/2026\", \"yyyy-MM-dd\")")]
    [TestCase("FormatDate(\"\", \"yyyy-MM-dd\")")]
    [TestCase("FormatDate(mv[\"Missing\"], \"yyyy-MM-dd\")")]
    public void Evaluate_FormatDateOfNoDate_GivesNoValue(string expression)
    {
        Assert.That(Evaluate(expression), Is.Null);
    }

    [Test]
    public void Evaluate_FormatDateWithACast_StillWorks()
    {
        var result = Evaluate("FormatDate((DateTime)mv[\"HireDate\"], \"yyyy\")", new Dictionary<string, object?> { ["HireDate"] = March15 });

        Assert.That(result, Is.EqualTo("2026"));
    }

    #endregion

    #region ParseDate

    [Test]
    public void Evaluate_ParseDateMatchingTheFormat_GivesThatDateAsUtc()
    {
        var result = Evaluate("ParseDate(\"15/03/2026\", \"dd/MM/yyyy\")");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(((DateTime)result!).Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void Evaluate_ParseDateWithAnOffsetInTheFormat_HonoursTheOffset()
    {
        var result = Evaluate("ParseDate(\"15/03/2026 11:30 +01:00\", \"dd/MM/yyyy HH:mm zzz\")");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(March15));
            Assert.That(((DateTime)result!).Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void Evaluate_ParseDateOfNoValue_GivesNoValue()
    {
        Assert.That(Evaluate("ParseDate(mv[\"Missing\"], \"dd/MM/yyyy\")"), Is.Null);
    }

    [Test]
    public void Evaluate_ParseDateOfEmptyText_GivesNoValue()
    {
        Assert.That(Evaluate("ParseDate(\"\", \"dd/MM/yyyy\")"), Is.Null);
    }

    [Test]
    public void Evaluate_ParseDateNotMatchingTheFormat_IsAnExpressionError()
    {
        // An error, never null: null would clear the attribute, and a value the source sent in another form is not
        // the source having no value.
        Assert.That(() => Evaluate("ParseDate(\"2026-03-15\", \"dd/MM/yyyy\")"),
            Throws.TypeOf<ExpressionEvaluationException>().With.InnerException.TypeOf<FormatException>());
    }

    [Test]
    public void Evaluate_ParseDateNotMatchingTheFormat_ErrorDoesNotRepeatTheText()
    {
        // The error reaches the worker and Drift Detection logs, which must never carry source data such as a date of
        // birth; the Run Profile Execution Item already identifies the object whose value did not match.
        var error = Assert.Throws<ExpressionEvaluationException>(() =>
            Evaluate("ParseDate(cs[\"birthDate\"], \"dd/MM/yyyy\")", cs: new Dictionary<string, object?> { ["birthDate"] = "1984-07-21" }))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Message, Does.Not.Contain("1984-07-21"));
            Assert.That(error.Message, Does.Contain("dd/MM/yyyy"), "the format is configuration, not data, and says what was expected");
        }
    }

    [Test]
    public void Evaluate_ParseDateOfADate_GivesItAsUtc()
    {
        var unspecified = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Unspecified);

        var result = Evaluate("ParseDate(mv[\"When\"], \"dd/MM/yyyy\")", new Dictionary<string, object?> { ["When"] = unspecified });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(March15));
            Assert.That(((DateTime)result!).Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    #endregion
}
