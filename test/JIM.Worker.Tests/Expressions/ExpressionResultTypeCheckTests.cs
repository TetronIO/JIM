// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// Saving refuses an expression whose result its target can never hold, where that can be known before it runs (#2055
/// Phase 3): only when the outermost call is a catalogue function, and only for a type the conversion synchronisation
/// runs never accepts. Anything unknown passes, so the check cannot refuse an expression that would have worked.
/// </summary>
[TestFixture]
public class ExpressionResultTypeCheckTests
{
    [TestCase("Upper(cs[\"sn\"])", ExpressionResultType.Text)]
    [TestCase("  Now()  ", ExpressionResultType.DateTime)]
    [TestCase("Trim(Upper(cs[\"sn\"]))", ExpressionResultType.Text)]
    [TestCase("HasValue(cs[\"manager\"])", ExpressionResultType.Boolean)]
    [TestCase("Lower(\")\" + cs[\"sn\"])", ExpressionResultType.Text)]
    [TestCase("ToFileTime(mv[\"Expiry\"])", ExpressionResultType.LongNumber)]
    public void ResultTypeOf_OutermostCallIsACatalogueFunction_IsItsDeclaredType(string expression, ExpressionResultType expected)
    {
        Assert.That(ExpressionResultTypeCheck.ResultTypeOf(expression), Is.EqualTo(expected));
    }

    [TestCase("Upper(cs[\"sn\"]) + \"x\"")]
    [TestCase("cs[\"sn\"]")]
    [TestCase("\"Upper(x)\"")]
    [TestCase("IIF(HasValue(cs[\"sn\"]), 1, \"none\")")]
    [TestCase("Lower(cs[\"a\"]) + Lower(cs[\"b\"])")]
    [TestCase("Unknown(cs[\"sn\"])")]
    [TestCase("cs[\"sn\"].ToUpper()")]
    [TestCase("")]
    public void ResultTypeOf_AnythingElse_IsUnknown(string expression)
    {
        Assert.That(ExpressionResultTypeCheck.ResultTypeOf(expression), Is.EqualTo(ExpressionResultType.Unknown));
    }

    [TestCase(ExpressionResultType.Boolean, AttributeDataType.Number)]
    [TestCase(ExpressionResultType.Boolean, AttributeDataType.DateTime)]
    [TestCase(ExpressionResultType.DateTime, AttributeDataType.LongNumber)]
    [TestCase(ExpressionResultType.Number, AttributeDataType.Boolean)]
    [TestCase(ExpressionResultType.Text, AttributeDataType.Binary)]
    [TestCase(ExpressionResultType.Text, AttributeDataType.Reference)]
    public void CanNeverHold_ATypeTheConversionNeverAccepts_IsTrue(ExpressionResultType resultType, AttributeDataType target)
    {
        Assert.That(ExpressionResultTypeCheck.CanNeverHold(resultType, target), Is.True);
    }

    [TestCase(ExpressionResultType.Text, AttributeDataType.Number)]
    [TestCase(ExpressionResultType.Text, AttributeDataType.DateTime)]
    [TestCase(ExpressionResultType.Decimal, AttributeDataType.Number)]
    [TestCase(ExpressionResultType.Number, AttributeDataType.Text)]
    [TestCase(ExpressionResultType.LongNumber, AttributeDataType.Number)]
    [TestCase(ExpressionResultType.Unknown, AttributeDataType.Binary)]
    public void CanNeverHold_ATypeSomeValueOfWhichConverts_IsFalse(ExpressionResultType resultType, AttributeDataType target)
    {
        Assert.That(ExpressionResultTypeCheck.CanNeverHold(resultType, target), Is.False);
    }

    /// <summary>Values of each type that cover every way the conversion can accept one, edge cases included.</summary>
    private static readonly Dictionary<ExpressionResultType, object[]> Samples = new()
    {
        [ExpressionResultType.Text] = ["x", "1", "-1", "1.5", "true", "2026-03-02", "2026-03-02T10:00:00Z", Guid.NewGuid().ToString(), ""],
        [ExpressionResultType.Number] = [0, 1, -1, int.MaxValue, int.MinValue],
        [ExpressionResultType.LongNumber] = [0L, 1L, long.MaxValue, long.MinValue],
        [ExpressionResultType.Decimal] = [0m, 2m, 1.5m, -3m],
        [ExpressionResultType.DateTime] = [new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), DateTime.MinValue],
        [ExpressionResultType.Boolean] = [true, false]
    };

    private static IEnumerable<TestCaseData> EveryKnownTypeAndTarget() =>
        from resultType in Samples.Keys
        from target in Enum.GetValues<AttributeDataType>().Where(t => t != AttributeDataType.NotSet)
        select new TestCaseData(resultType, target).SetName($"{resultType} into {target}");

    [TestCaseSource(nameof(EveryKnownTypeAndTarget))]
    public void CanNeverHold_AgreesWithTheConversionSynchronisationRuns(ExpressionResultType resultType, AttributeDataType target)
    {
        // Refused exactly when no value of the type converts: a refusal of a type some value of which converts would
        // block an expression that works.
        var anyConverts = Samples[resultType].Any(sample => ExpressionResultConverter.TryConvert(sample, target, out _));

        Assert.That(ExpressionResultTypeCheck.CanNeverHold(resultType, target), Is.EqualTo(!anyConverts));
    }

    [Test]
    public void DescribeProblem_ProvableMismatch_NamesTheFunctionAndTheTarget()
    {
        var problem = ExpressionResultTypeCheck.DescribeProblem("HasValue(cs[\"manager\"])", AttributeDataType.DateTime, "Hire Date");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem, Does.Contain("HasValue"));
            Assert.That(problem, Does.Contain("Hire Date"));
        }
    }

    [TestCase("Upper(cs[\"sn\"])", AttributeDataType.Number)]
    [TestCase("Coalesce(cs[\"a\"], cs[\"b\"])", AttributeDataType.Binary)]
    [TestCase("cs[\"sn\"]", AttributeDataType.Boolean)]
    public void DescribeProblem_NothingProvable_IsNull(string expression, AttributeDataType target)
    {
        Assert.That(ExpressionResultTypeCheck.DescribeProblem(expression, target, "Target"), Is.Null);
    }
}
