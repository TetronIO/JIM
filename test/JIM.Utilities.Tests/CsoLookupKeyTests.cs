// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities.Tests;

/// <summary>
/// #2705: which External ID keys a Delta Import may ask the database about, rather than loading every object JIM holds.
/// </summary>
public class CsoLookupKeyTests
{
    [TestCase(true, "JBloggs")]
    [TestCase(true, "ÉMILE")]
    [TestCase(false, "jbloggs")]
    [TestCase(false, "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [TestCase(false, "4200")]
    public void CanBeLookedUpInDatabase_CaseExactOrAsciiKey_IsTrue(bool caseExact, string valueKey)
    {
        Assert.That(CsoLookupKey.CanBeLookedUpInDatabase(caseExact, valueKey), Is.True);
    }

    [TestCase("émile")]
    [TestCase("ref-\uA7CB")]
    public void CanBeLookedUpInDatabase_KeyIgnoringCaseOutsideAscii_IsFalse(string valueKey)
    {
        Assert.That(CsoLookupKey.CanBeLookedUpInDatabase(caseExact: false, valueKey), Is.False,
            "Only JIM's own folding compares this key reliably, so the whole lookup must answer it");
    }

    [TestCase(false, "jking")]
    [TestCase(false, "id-1")]
    public void DependsOnDatabaseFolding_KeyIgnoringCaseWithALetterFoldedFromOutsideAscii_IsTrue(bool caseExact, string valueKey)
    {
        Assert.That(CsoLookupKey.DependsOnDatabaseFolding(caseExact, valueKey), Is.True,
            "A stored value spelt with İ or the Kelvin sign keys the same, and only a database that folds them as JIM does finds it");
    }

    [TestCase(true, "jking")]
    [TestCase(false, "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [TestCase(false, "4200")]
    [TestCase(false, "jbloggs")]
    public void DependsOnDatabaseFolding_CaseExactOrNoLetterFoldedFromOutsideAscii_IsFalse(bool caseExact, string valueKey)
    {
        Assert.That(CsoLookupKey.DependsOnDatabaseFolding(caseExact, valueKey), Is.False,
            "Every database's lower() folds ASCII, so a GUID or a number is found on any of them");
    }
}
