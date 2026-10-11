// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Models.Tests.Sync;

/// <summary>
/// <see cref="SequenceSkipQuery.TryParseHeldNumber"/> (#2031): a held value counts as holding a sequence number only
/// when it is written exactly as the sequence writes that number, so a skip over held numbers never treats a value the
/// sequence could not have produced as one of its own.
/// </summary>
[TestFixture]
public class SequenceSkipQueryTests
{
    [TestCase("emp-001000", 1000L)]
    [TestCase("emp-1234567", 1234567L)] // Outgrew the width: written unpadded, as "Allow longer" writes it.
    public void TryParseHeldNumber_WrittenAsTheSequenceWritesIt_ReturnsTheNumber(string value, long expected)
    {
        var query = Query(prefix: "emp-", fixedWidth: 6);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query.TryParseHeldNumber(value, out var number), Is.True);
            Assert.That(number, Is.EqualTo(expected));
        }
    }

    [TestCase("emp-1000")]      // Not padded to the width.
    [TestCase("emp-0001000")]   // Padded past it.
    [TestCase("emp-")]          // No number.
    [TestCase("emp-00100a")]    // Not all digits.
    [TestCase("emp-00-100")]    // Not all digits.
    [TestCase("abc-001000")]    // Another prefix.
    [TestCase("emp-001000x")]   // Trailing text.
    [TestCase("emp-1234567890123456789")] // Too long to be a number.
    public void TryParseHeldNumber_NotWrittenAsTheSequenceWritesAnyNumber_ReturnsFalse(string value)
    {
        Assert.That(Query(prefix: "emp-", fixedWidth: 6).TryParseHeldNumber(value, out _), Is.False);
    }

    [Test]
    public void TryParseHeldNumber_SuffixAroundTheNumber_ReadsTheNumberBetweenThem()
    {
        var query = Query(prefix: "joe.", suffix: "@example.com");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query.TryParseHeldNumber("joe.17@example.com", out var number), Is.True);
            Assert.That(number, Is.EqualTo(17));
            Assert.That(query.TryParseHeldNumber("joe.017@example.com", out _), Is.False, "unpadded with no width: a leading zero is not how 17 is written");
            Assert.That(query.TryParseHeldNumber("joe.@example.com", out _), Is.False);
        }
    }

    [Test]
    public void TryParseHeldNumber_NoPlacementAndNoWidth_ReadsABareNumber()
    {
        var query = Query();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query.TryParseHeldNumber("0", out var zero), Is.True);
            Assert.That(zero, Is.EqualTo(0));
            Assert.That(query.TryParseHeldNumber("42", out var number), Is.True);
            Assert.That(number, Is.EqualTo(42));
            Assert.That(query.TryParseHeldNumber("042", out _), Is.False);
        }
    }

    [TestCase(42L, null, "emp-42@x")]
    [TestCase(42L, 6, "emp-000042@x")]
    [TestCase(1234567L, 6, "emp-1234567@x")]
    public void WriteNumber_WritesTheNumberAsTheSequenceDoes_AndReadsBackToItself(long number, int? fixedWidth, string expected)
    {
        var query = Query(prefix: "emp-", suffix: "@x", fixedWidth: fixedWidth);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query.WriteNumber(number), Is.EqualTo(expected));
            Assert.That(query.TryParseHeldNumber(query.WriteNumber(number), out var read), Is.True);
            Assert.That(read, Is.EqualTo(number));
        }
    }

    private static SequenceSkipQuery Query(string prefix = "", string suffix = "", int? fixedWidth = null) => new()
    {
        MetaverseAttributeId = 1,
        From = 1,
        Increment = 1,
        NumericTarget = false,
        Prefix = prefix,
        Suffix = suffix,
        FixedWidth = fixedWidth
    };
}
