// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities.Tests;

public class ReferenceValueIndexTests
{
    [Test]
    public void TryMatch_ValueIgnoringCase_MatchesInAnotherCase()
    {
        var index = new ReferenceValueIndex<int>();
        index.Register("CN=Joe,DC=PANOPLY", caseExact: false, 1);

        Assert.That(index.TryMatch("cn=joe,dc=panoply", out var target) ? target : 0, Is.EqualTo(1));
    }

    [Test]
    public void TryMatch_CaseExactValue_MatchesOnlyInItsOwnCase()
    {
        var index = new ReferenceValueIndex<int>();
        index.Register("Abc", caseExact: true, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.TryMatch("Abc", out _), Is.True);
            Assert.That(index.TryMatch("abc", out _), Is.False);
        }
    }

    [Test]
    public void TryMatch_TwoCaseExactValuesDifferingOnlyInCase_EachMatchesItsOwn()
    {
        var index = new ReferenceValueIndex<int>();
        index.Register("Abc", caseExact: true, 1);
        index.Register("abc", caseExact: true, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.TryMatch("Abc", out var first) ? first : 0, Is.EqualTo(1));
            Assert.That(index.TryMatch("abc", out var second) ? second : 0, Is.EqualTo(2));
        }
    }

    [Test]
    public void Values_AreTheRegisteredValuesAsGiven()
    {
        var index = new ReferenceValueIndex<int>();
        index.Register("CN=Joe", caseExact: false, 1);
        index.Register("Abc", caseExact: true, 2);

        Assert.That(index.Values, Is.EquivalentTo(new[] { "CN=Joe", "Abc" }));
    }
}
