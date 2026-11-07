// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// #2705: the import's External ID lookup either holds every object JIM holds, or holds the answers for the External IDs
/// it looked up for one page. An External ID it did not look up must never read as "no such object": the import would
/// create a duplicate of an object JIM already holds. It fails instead.
/// </summary>
[TestFixture]
public class CsoImportStateLookupTests
{
    private static readonly CsoImportStateLookupEntry Entry = new(Guid.NewGuid(), null, null, ConnectedSystemObjectStatus.Normal, null);

    [Test]
    public void TryGetValue_LookupOfEveryObject_AnswersAnyKey()
    {
        var lookup = CsoImportStateLookup.ForEveryObject(new Dictionary<string, CsoImportStateLookupEntry>(StringComparer.Ordinal) { ["held"] = Entry });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup.HoldsEveryObject, Is.True);
            Assert.That(lookup.TryGetValue("held", out var found), Is.True);
            Assert.That(found, Is.EqualTo(Entry));
            Assert.That(lookup.TryGetValue("never-looked-up", out _), Is.False, "Holding every object, a missing key is a new object");
            Assert.That(lookup.ContainsKey("never-looked-up"), Is.False);
        }
    }

    [Test]
    public void TryGetValue_KeyLookedUpForThePage_AnswersFoundAndNotFound()
    {
        var lookup = CsoImportStateLookup.ForLookedUpKeys(StringComparer.Ordinal);
        lookup.Add(["held", "new"], new Dictionary<string, CsoImportStateLookupEntry>(StringComparer.Ordinal) { ["held"] = Entry });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup.HoldsEveryObject, Is.False);
            Assert.That(lookup.TryGetValue("held", out var found), Is.True);
            Assert.That(found, Is.EqualTo(Entry));
            Assert.That(lookup.TryGetValue("new", out _), Is.False, "Looked up and not found: a new object");
            Assert.That(lookup.ContainsKey("new"), Is.False);
            Assert.That(lookup.LookedUpKeyCount, Is.EqualTo(2));
            Assert.That(lookup.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void TryGetValue_KeyNotLookedUpForThePage_ThrowsRatherThanReportingANewObject()
    {
        var lookup = CsoImportStateLookup.ForLookedUpKeys(StringComparer.Ordinal);
        lookup.Add(["held"], new Dictionary<string, CsoImportStateLookupEntry>(StringComparer.Ordinal) { ["held"] = Entry });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => lookup.TryGetValue("never-looked-up", out _), Throws.InvalidOperationException);
            Assert.That(() => lookup.ContainsKey("never-looked-up"), Throws.InvalidOperationException);
        }
    }

    [Test]
    public void Add_AnswerForAKeyNotAskedAbout_Throws()
    {
        var lookup = CsoImportStateLookup.ForLookedUpKeys(StringComparer.Ordinal);

        Assert.That(() => lookup.Add(["asked"], new Dictionary<string, CsoImportStateLookupEntry>(StringComparer.Ordinal) { ["not-asked"] = Entry }),
            Throws.InvalidOperationException);
    }
}
