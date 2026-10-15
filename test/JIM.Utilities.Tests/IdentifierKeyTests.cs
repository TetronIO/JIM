// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Utilities.Tests;

/// <summary>
/// How JIM compares two values when it asks "are these the same thing?" (#2051): ignoring case, unless the attribute
/// is declared case-exact. Everything else (whether a value has changed) stays exact.
/// </summary>
public class IdentifierKeyTests
{
    [Test]
    public void For_NotCaseExact_FoldsCase()
    {
        Assert.That(IdentifierKey.For("JBloggs", caseExact: false), Is.EqualTo(IdentifierKey.For("jbloggs", caseExact: false)));
    }

    [Test]
    public void For_NotCaseExact_LowersTheTurkishDottedCapitalIAsPostgresDoes()
    {
        Assert.That(IdentifierKey.For("\u0130STANBUL", caseExact: false), Is.EqualTo("istanbul"));
    }

    private static readonly string[][] SamePairsIgnoringCase =
    [
        ["JBloggs", "jbloggs"],
        ["\u0130STANBUL", "istanbul"],
        ["\u00C9MILE", "\u00E9mile"],
        ["STRA\u1E9EE", "stra\u00DFe"],
        ["\u01C5URO", "\u01C6uro"]
    ];

    /// <summary>
    /// One rule, one answer: a comparer ignoring case calls two values the same exactly when their keys are equal, so a
    /// key lookup can never join values that a comparison or a set calls different (#2051).
    /// </summary>
    [TestCaseSource(nameof(SamePairsIgnoringCase))]
    public void Comparer_IgnoringCase_AgreesWithFor(string first, string second)
    {
        var comparer = IdentifierKey.Comparer(caseExact: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IdentifierKey.For(first, caseExact: false), Is.EqualTo(IdentifierKey.For(second, caseExact: false)));
            Assert.That(comparer.Equals(first, second), Is.True);
            Assert.That(comparer.GetHashCode(first), Is.EqualTo(comparer.GetHashCode(second)));
            Assert.That(IdentifierKey.AreSame(first, second, caseExact: false), Is.True);
        }
    }

    [Test]
    public void Comparer_IgnoringCase_KeepsDifferentValuesApart()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IdentifierKey.Comparer(caseExact: false).Equals("JBloggs", "JBlogg"), Is.False);
            Assert.That(IdentifierKey.AreSame("istanbul", "\u0131stanbul", caseExact: false), Is.False,
                "the dotless i is a different letter");
            Assert.That(IdentifierKey.AreSame("JBloggs", "jbloggs", caseExact: true), Is.False);
            Assert.That(IdentifierKey.AreSame(null, null, caseExact: false), Is.True);
            Assert.That(IdentifierKey.AreSame("a", null, caseExact: false), Is.False);
        }
    }

    [Test]
    public void For_CaseExact_KeepsTheValue()
    {
        Assert.That(IdentifierKey.For("JBloggs", caseExact: true), Is.EqualTo("JBloggs"));
    }

    [Test]
    public void Comparer_MatchesFor()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IdentifierKey.Comparer(caseExact: false).Equals("DC=PANOPLY", "dc=panoply"), Is.True);
            Assert.That(IdentifierKey.Comparer(caseExact: true).Equals("DC=PANOPLY", "dc=panoply"), Is.False);
            Assert.That(IdentifierKey.AreSame("DC=PANOPLY", "dc=panoply", caseExact: false), Is.True);
            Assert.That(IdentifierKey.AreSame("DC=PANOPLY", "dc=panoply", caseExact: true), Is.False);
        }
    }

    [Test]
    public void ChangeComparison_TheSecondaryExternalId_IgnoresCase()
    {
        // An LDAP distinguished name: the directory reports it in the case it stored, which for its parent
        // components is the directory's own (#2195). A case-only difference names the same entry.
        var distinguishedName = new ConnectedSystemObjectTypeAttribute { Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true };

        Assert.That(AttributeValueComparison.AreSame(distinguishedName, "DC=PANOPLY", "dc=panoply"), Is.True);
    }

    [Test]
    public void ChangeComparison_ACaseExactExternalId_IsExact()
    {
        var scimId = new ConnectedSystemObjectTypeAttribute { Name = "id", Type = AttributeDataType.Text, IsExternalId = true, CaseExact = true };

        Assert.That(AttributeValueComparison.AreSame(scimId, "Abc", "abc"), Is.False);
    }

    [Test]
    public void ChangeComparison_AReference_IgnoresCase()
    {
        // A reference's value is the object it points to; an unresolved one is compared as that object's identifier.
        var member = new ConnectedSystemObjectTypeAttribute { Name = "member", Type = AttributeDataType.Reference };

        Assert.That(AttributeValueComparison.AreSame(member, "CN=Joe", "cn=joe"), Is.True);
    }

    [Test]
    public void ChangeComparison_OrdinaryText_IsExact()
    {
        // A change of case is a change: a display name stored as given must have its new case exported.
        var displayName = new ConnectedSystemObjectTypeAttribute { Name = "displayName", Type = AttributeDataType.Text };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeValueComparison.AreSame(displayName, "Joe Bloggs", "joe bloggs"), Is.False);
            Assert.That(AttributeValueComparison.AreSame(null, "Joe Bloggs", "joe bloggs"), Is.False);
            Assert.That(AttributeValueComparison.ComparerFor(displayName).Equals("Joe Bloggs", "joe bloggs"), Is.False);
        }
    }
}
