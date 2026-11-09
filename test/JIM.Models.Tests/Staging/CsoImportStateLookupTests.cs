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

    /// <summary>
    /// #2835: a page's second pass asks only for keys its first pass did not, so it can tell which those are without
    /// asking for an answer (which throws for a key never looked up).
    /// </summary>
    [Test]
    public void WasLookedUp_KeysLookedUpAndNot_SaysWhichWithoutThrowing()
    {
        var page = CsoImportStateLookup.ForLookedUpKeys(StringComparer.Ordinal);
        page.Add(["held", "new"], new Dictionary<string, CsoImportStateLookupEntry>(StringComparer.Ordinal) { ["held"] = Entry });
        var every = CsoImportStateLookup.ForEveryObject(new Dictionary<string, CsoImportStateLookupEntry>(StringComparer.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.WasLookedUp("held"), Is.True);
            Assert.That(page.WasLookedUp("new"), Is.True, "Looked up and not found is still looked up");
            Assert.That(page.WasLookedUp("never-looked-up"), Is.False);
            Assert.That(every.WasLookedUp("anything"), Is.True, "Holding every object, every key is answered");
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
