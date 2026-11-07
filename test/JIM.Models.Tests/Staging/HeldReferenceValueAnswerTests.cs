// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// #2817: an answer about a group's held members stands in for the members it did not load only where the database found
/// every member the import's comparison would match ignoring case. The comparison never equates a character outside ASCII
/// with one inside it, so that holds for an ASCII value once the database is known to fold ASCII as JIM does, and is never
/// assumed for anything else.
/// </summary>
[TestFixture]
public class HeldReferenceValueAnswerTests
{
    [Test]
    public void IsCompleteIgnoringCase_EveryImportedValueMatchedExactly_IsCompleteWhateverTheDatabaseFolds()
    {
        var answer = Answer();

        Assert.That(answer.IsCompleteIgnoringCase(databaseFoldsCaseLikeIdentifierKey: false), Is.True);
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public void IsCompleteIgnoringCase_UnmatchedAsciiValue_IsCompleteOnlyWhereTheDatabaseFoldsAsJimDoes(bool databaseFolds, bool expected)
    {
        var answer = Answer("UID=USER7,OU=People");

        Assert.That(answer.IsCompleteIgnoringCase(databaseFolds), Is.EqualTo(expected));
    }

    [Test]
    public void IsCompleteIgnoringCase_UnmatchedValueOutsideAscii_IsNeverComplete()
    {
        var answer = Answer("uid=user7,ou=People", "uid=Zoë,ou=People");

        Assert.That(answer.IsCompleteIgnoringCase(databaseFoldsCaseLikeIdentifierKey: true), Is.False);
    }

    /// <summary>
    /// The premise the rule rests on, checked against the runtime rather than assumed: no character outside ASCII equals
    /// one inside it ignoring case, ordinally, which is how the comparison matches a member to an imported DN.
    /// </summary>
    [Test]
    public void OrdinalIgnoreCase_NoCharacterOutsideAscii_EqualsOneInsideIt()
    {
        for (var code = 0x80; code < 0x10000; code++)
        {
            if (code is >= 0xD800 and <= 0xDFFF)
                continue;

            var outside = ((char)code).ToString();
            for (var ascii = 0; ascii < 0x80; ascii++)
            {
                if (string.Equals(outside, ((char)ascii).ToString(), StringComparison.OrdinalIgnoreCase))
                    Assert.Fail($"U+{code:X4} equals U+{ascii:X4} ignoring case");
            }
        }
    }

    private static HeldReferenceValueAnswer Answer(params string[] unmatchedIncoming)
    {
        var answer = new HeldReferenceValueAnswer(new HeldReferenceValueQuestion(Guid.NewGuid(), 1, ["uid=a"]));
        foreach (var value in unmatchedIncoming)
            answer.UnmatchedIncomingValues.Add(value);
        return answer;
    }
}
