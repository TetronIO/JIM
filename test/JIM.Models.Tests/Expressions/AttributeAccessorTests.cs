// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Expressions;
using NUnit.Framework;

namespace JIM.Models.Tests.Expressions;

/// <summary>
/// What <c>cs["x"]</c> and <c>mv["x"]</c> read (#2053): every caller's dictionary reads the same way, whether it holds
/// a single value, the canonical several values the synchronisation builders produce, or a plain list (the
/// test-expression API and Example Data supply those).
/// </summary>
[TestFixture]
public class AttributeAccessorTests
{
    [Test]
    public void Indexer_AListOfSeveralValues_ReadsAsEveryValueInOrder()
    {
        var accessor = new AttributeAccessor(new Dictionary<string, object?> { ["proxyAddresses"] = new List<object?> { "b", "a" } });

        var read = accessor["proxyAddresses"];

        Assert.That(read, Is.InstanceOf<ExpressionValues>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That((ExpressionValues)read!, Is.EqualTo(new object[] { "a", "b" }));
            Assert.That(((ExpressionValues)read!).SourceName, Is.EqualTo("proxyAddresses"));
        }
    }

    [Test]
    public void Indexer_AnArrayOfOneValue_ReadsAsThatValue()
    {
        var accessor = new AttributeAccessor(new Dictionary<string, object?> { ["mail"] = new[] { "a@example.org" } });

        Assert.That(accessor["mail"], Is.EqualTo("a@example.org"));
    }

    [Test]
    public void Indexer_AnEmptyList_ReadsAsNoValue()
    {
        var accessor = new AttributeAccessor(new Dictionary<string, object?> { ["mail"] = new List<object?>() });

        Assert.That(accessor["mail"], Is.Null);
    }

    [Test]
    public void Indexer_TextAndBinary_AreEachOneValue()
    {
        var accessor = new AttributeAccessor(new Dictionary<string, object?> { ["cn"] = "Priya", ["photo"] = new byte[] { 1 } });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accessor["cn"], Is.EqualTo("Priya"));
            Assert.That(accessor["photo"], Is.EqualTo(new byte[] { 1 }));
        }
    }

    [Test]
    public void Indexer_SeveralBinaryValues_EachReadIsACopy()
    {
        // #2153 N6, for several values: an expression changing its bytes never changes the attribute value.
        var stored = new byte[] { 1 };
        var accessor = new AttributeAccessor(new Dictionary<string, object?> { ["certs"] = new List<object?> { stored, new byte[] { 2 } } });

        var first = (byte[])((ExpressionValues)accessor["certs"]!)[0]!;
        first[0] = 99;

        Assert.That(stored[0], Is.EqualTo(1));
    }

    [Test]
    public void Indexer_ValuesAlreadyRead_AreReadAsTheyAre()
    {
        var values = new ExpressionValues(["b", "a"], "x");
        var accessor = new AttributeAccessor(new Dictionary<string, object?> { ["x"] = values });

        Assert.That(accessor["x"], Is.SameAs(values));
    }
}
