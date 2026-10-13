// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Expressions;
using NUnit.Framework;

namespace JIM.Models.Tests.Expressions;

/// <summary>
/// How an expression reads a multi-valued attribute (#2053, PRD Phase 2 requirement 8): no value as null, one value
/// as itself, several values as every distinct value in a fixed order, and several values used where one is needed
/// as an error naming the attribute and the count.
/// </summary>
[TestFixture]
public class ExpressionValuesTests
{
    [Test]
    public void FromAttribute_NoValues_ReadsAsNull()
    {
        Assert.That(ExpressionValues.FromAttribute("mail", []), Is.Null);
    }

    [Test]
    public void FromAttribute_OneValue_ReadsAsItself()
    {
        Assert.That(ExpressionValues.FromAttribute("mail", ["a@example.org"]), Is.EqualTo("a@example.org"));
    }

    [Test]
    public void FromAttribute_TheSameValueTwice_ReadsAsOneValue()
    {
        Assert.That(ExpressionValues.FromAttribute("mail", ["a@example.org", "a@example.org"]), Is.EqualTo("a@example.org"));
    }

    [Test]
    public void FromAttribute_NullsAmongValues_AreNotValues()
    {
        Assert.That(ExpressionValues.FromAttribute("mail", [null, "a@example.org", null]), Is.EqualTo("a@example.org"));
    }

    [Test]
    public void FromAttribute_SeveralTextValues_ReadInOrdinalOrder()
    {
        // Ordinal: upper case sorts before lower case, so the order is the same on every server and every import.
        var read = ExpressionValues.FromAttribute("proxyAddresses", ["smtp:p.shah@example.org", "SMTP:priya.shah@example.org", "X500:/o=Example"]);

        Assert.That(read, Is.InstanceOf<ExpressionValues>());
        Assert.That((ExpressionValues)read!, Is.EqualTo(new object[] { "SMTP:priya.shah@example.org", "X500:/o=Example", "smtp:p.shah@example.org" }));
    }

    [Test]
    public void FromAttribute_SeveralNumbers_ReadAscending()
    {
        Assert.That((ExpressionValues)ExpressionValues.FromAttribute("level", [10, 2, 33])!, Is.EqualTo(new object[] { 2, 10, 33 }));
    }

    [Test]
    public void FromAttribute_SeveralDates_ReadOldestFirst()
    {
        var later = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var earlier = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.That((ExpressionValues)ExpressionValues.FromAttribute("dates", [later, earlier])!, Is.EqualTo(new object[] { earlier, later }));
    }

    [Test]
    public void FromAttribute_SeveralGuids_ReadByTheirText()
    {
        var b = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
        var a = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");

        Assert.That((ExpressionValues)ExpressionValues.FromAttribute("ids", [b, a])!, Is.EqualTo(new object[] { a, b }));
    }

    [Test]
    public void FromAttribute_TheSameBinaryContentTwice_ReadsAsOneValue()
    {
        Assert.That(ExpressionValues.FromAttribute("photo", [new byte[] { 1, 2 }, new byte[] { 1, 2 }]), Is.EqualTo(new byte[] { 1, 2 }));
    }

    [Test]
    public void FromAttribute_SeveralValues_KeepsTheAttributeNameForItsErrors()
    {
        var read = (ExpressionValues)ExpressionValues.FromAttribute("proxyAddresses", ["a", "b"])!;

        Assert.That(read.SourceName, Is.EqualTo("proxyAddresses"));
    }

    [Test]
    public void ToString_SeveralValues_IsTheErrorNamingTheAttributeAndCount()
    {
        // Joining several values to text with + is a one-value position; the language reaches it through ToString.
        var values = new ExpressionValues(["a", "b"], "proxyAddresses");

        var ex = Assert.Throws<ExpressionEvaluationException>(() => _ = values.ToString());

        Assert.That(ex!.Message, Does.Contain("proxyAddresses").And.Contain("2 values").And.Contain("Join"));
    }

    [Test]
    public void ToString_OneValue_IsThatValuesText()
    {
        Assert.That(new ExpressionValues(["a"], null).ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void ToString_NoValues_IsEmptyText()
    {
        Assert.That(new ExpressionValues([], null).ToString(), Is.Empty);
    }

    [Test]
    public void SeveralValuesError_WithoutASourceName_DescribesTheValuesInstead()
    {
        var ex = ExpressionValues.SeveralValuesError(3, null);

        Assert.That(ex.Message, Does.StartWith("The expression has 3 values"));
    }

    [Test]
    public void SeveralValuesError_NeverCarriesTheValues()
    {
        var values = new ExpressionValues(["secret-one", "secret-two"], "proxyAddresses");

        var ex = Assert.Throws<ExpressionEvaluationException>(() => _ = values.ToString());

        Assert.That(ex!.Message, Does.Not.Contain("secret"));
    }

    [Test]
    public void EqualityWithNull_IsAPresenceTest()
    {
        var values = new ExpressionValues(["a", "b"], "x");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values == (object?)null, Is.False);
            Assert.That(values != (object?)null, Is.True);
        }
    }

    [Test]
    public void EqualityWithAValue_SeveralValues_IsTheError()
    {
        var values = new ExpressionValues(["a", "b"], "proxyAddresses");

        Assert.Throws<ExpressionEvaluationException>(() => _ = values == (object)"a");
    }

    [Test]
    public void EqualityWithAValue_OneValue_ComparesThatValue()
    {
        var values = new ExpressionValues(["a"], null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values == (object)"a", Is.True);
            Assert.That(values == (object)"b", Is.False);
        }
    }

    [Test]
    public void OrderingOperator_SeveralValues_IsTheError()
    {
        var values = new ExpressionValues([1, 2], "level");

        Assert.Throws<ExpressionEvaluationException>(() => _ = values > (object)1);
    }

    [Test]
    public void ArithmeticOperator_OneValue_UsesThatValue()
    {
        var values = new ExpressionValues([5], null);

        Assert.That(values + (object)1, Is.EqualTo(6));
    }
}
