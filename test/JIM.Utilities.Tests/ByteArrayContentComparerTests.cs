// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities.Tests;

public class ByteArrayContentComparerTests
{
    private static readonly ByteArrayContentComparer Comparer = ByteArrayContentComparer.Instance;

    [Test]
    public void Equals_TwoArraysHoldingTheSameBytes_ReturnsTrueWithEqualHashCodes()
    {
        byte[] a = [1, 2, 3];
        byte[] b = [1, 2, 3];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Comparer.Equals(a, b), Is.True);
            Assert.That(Comparer.GetHashCode(a), Is.EqualTo(Comparer.GetHashCode(b)));
        }
    }

    [Test]
    public void Equals_DifferentBytesOrLengths_ReturnsFalse()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Comparer.Equals([1, 2, 3], [1, 2, 4]), Is.False);
            Assert.That(Comparer.Equals([1, 2, 3], [1, 2]), Is.False);
        }
    }

    [Test]
    public void Equals_NullOperands_OnlyEqualEachOther()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Comparer.Equals(null, null), Is.True);
            Assert.That(Comparer.Equals(null, []), Is.False);
            Assert.That(Comparer.Equals([], null), Is.False);
        }
    }

    [Test]
    public void HashSet_ArraysHoldingTheSameBytes_HoldsOneValue()
    {
        var set = new HashSet<byte[]>(Comparer) { new byte[] { 9, 8 }, new byte[] { 9, 8 }, new byte[] { 7 } };

        Assert.That(set, Has.Count.EqualTo(2));
    }
}
