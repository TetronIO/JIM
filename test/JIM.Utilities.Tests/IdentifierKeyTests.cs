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
            Assert.That(string.Equals("DC=PANOPLY", "dc=panoply", IdentifierKey.Comparison(caseExact: false)), Is.True);
            Assert.That(string.Equals("DC=PANOPLY", "dc=panoply", IdentifierKey.Comparison(caseExact: true)), Is.False);
        }
    }

    [Test]
    public void ChangeComparison_TheSecondaryExternalId_IgnoresCase()
    {
        // An LDAP distinguished name: the directory reports it in the case it stored, which for its parent
        // components is the directory's own (#2195). A case-only difference names the same entry.
        var distinguishedName = new ConnectedSystemObjectTypeAttribute { Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true };

        Assert.That(AttributeValueComparison.For(distinguishedName), Is.EqualTo(StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ChangeComparison_ACaseExactExternalId_IsExact()
    {
        var scimId = new ConnectedSystemObjectTypeAttribute { Name = "id", Type = AttributeDataType.Text, IsExternalId = true, CaseExact = true };

        Assert.That(AttributeValueComparison.For(scimId), Is.EqualTo(StringComparison.Ordinal));
    }

    [Test]
    public void ChangeComparison_AReference_IgnoresCase()
    {
        // A reference's value is the object it points to; an unresolved one is compared as that object's identifier.
        var member = new ConnectedSystemObjectTypeAttribute { Name = "member", Type = AttributeDataType.Reference };

        Assert.That(AttributeValueComparison.For(member), Is.EqualTo(StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ChangeComparison_OrdinaryText_IsExact()
    {
        // A change of case is a change: a display name stored as given must have its new case exported.
        var displayName = new ConnectedSystemObjectTypeAttribute { Name = "displayName", Type = AttributeDataType.Text };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeValueComparison.For(displayName), Is.EqualTo(StringComparison.Ordinal));
            Assert.That(AttributeValueComparison.For(null), Is.EqualTo(StringComparison.Ordinal));
        }
    }
}
