// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// An entry states its object classes in no defined order, and once auxiliary classes can be selected as Object Types
/// an entry can match more than one of them. These cover what it then imports as, and which of the searches that
/// returned it is the one that emits it.
/// </summary>
[TestFixture]
public class LdapObjectTypeMatcherTests
{
    private ConnectedSystemObjectType _inetOrgPerson = null!;
    private ConnectedSystemObjectType _posixAccount = null!;
    private ConnectedSystemObjectType _person = null!;

    [SetUp]
    public void SetUp()
    {
        _inetOrgPerson = StructuralType("inetOrgPerson");
        _person = StructuralType("person");
        _posixAccount = AuxiliaryType("posixAccount");
    }

    #region Match

    [Test]
    public void Match_AuxiliaryClassListedBeforeTheStructuralOne_ReturnsTheStructuralType()
    {
        var matched = LdapObjectTypeMatcher.Match(
            ["top", "posixAccount", "inetOrgPerson"],
            [_inetOrgPerson, _posixAccount]);

        Assert.That(matched, Is.SameAs(_inetOrgPerson));
    }

    [Test]
    public void Match_AuxiliaryClassListedAfterTheStructuralOne_ReturnsTheSameStructuralType()
    {
        var matched = LdapObjectTypeMatcher.Match(
            ["top", "inetOrgPerson", "posixAccount"],
            [_inetOrgPerson, _posixAccount]);

        Assert.That(matched, Is.SameAs(_inetOrgPerson));
    }

    [Test]
    public void Match_OnlyAnAuxiliaryClassIsSelected_ReturnsTheAuxiliaryType()
    {
        var matched = LdapObjectTypeMatcher.Match(
            ["top", "inetOrgPerson", "posixAccount"],
            [_posixAccount]);

        Assert.That(matched, Is.SameAs(_posixAccount));
    }

    [Test]
    public void Match_SeveralStructuralClassesSelected_KeepsTheDirectorysOrderOfPrecedence()
    {
        // Active Directory returns objectClass most specific first, which is the only statement of specificity
        // JIM has, so among structural classes the first one still wins.
        var user = StructuralType("user");
        var matched = LdapObjectTypeMatcher.Match(
            ["user", "organizationalPerson", "person", "top"],
            [_person, user]);

        Assert.That(matched, Is.SameAs(user));
    }

    [Test]
    public void Match_UnclassifiedTypesAreNotTreatedAsAuxiliary()
    {
        // A Connected System that classifies nothing (the Active Directory path) must behave as it always has.
        var unclassified = new ConnectedSystemObjectType { Name = "user", Selected = true };
        var matched = LdapObjectTypeMatcher.Match(
            ["user", "top"],
            [unclassified, _posixAccount]);

        Assert.That(matched, Is.SameAs(unclassified));
    }

    [Test]
    public void Match_TheOnlyMatchingTypeIsNotSelected_ReturnsNull()
    {
        _inetOrgPerson.Selected = false;

        var matched = LdapObjectTypeMatcher.Match(["top", "inetOrgPerson"], [_inetOrgPerson]);

        Assert.That(matched, Is.Null);
    }

    [Test]
    public void Match_ObjectClassCasingDiffersFromTheSchema_StillMatches()
    {
        var matched = LdapObjectTypeMatcher.Match(["INETORGPERSON"], [_inetOrgPerson]);

        Assert.That(matched, Is.SameAs(_inetOrgPerson));
    }

    [Test]
    public void Match_NoObjectClassMatchesASelectedType_ReturnsNull()
    {
        var matched = LdapObjectTypeMatcher.Match(["top", "device"], [_inetOrgPerson, _posixAccount]);

        Assert.That(matched, Is.Null);
    }

    #endregion

    #region OwnsEntry

    [Test]
    public void OwnsEntry_TheEntryResolvedToTheTypeBeingSearchedFor_ReturnsTrue()
    {
        Assert.That(LdapObjectTypeMatcher.OwnsEntry(_inetOrgPerson, _inetOrgPerson), Is.True);
    }

    [Test]
    public void OwnsEntry_TheEntryResolvedToADifferentType_ReturnsFalse()
    {
        Assert.That(LdapObjectTypeMatcher.OwnsEntry(_inetOrgPerson, _posixAccount), Is.False);
    }

    [Test]
    public void OwnsEntry_NoTypeWasSearchedFor_ReturnsTrue()
    {
        // Fetching one object by its DN is not a per-type search, so there is nothing to defer to.
        Assert.That(LdapObjectTypeMatcher.OwnsEntry(_inetOrgPerson, null), Is.True);
    }

    [Test]
    public void MatchAndOwnsEntry_EntryCarryingTwoSelectedClasses_IsEmittedByExactlyOneSearch()
    {
        // A full import runs one search per selected Object Type, and this entry is returned by both of them.
        // Emitting it twice would stage one directory entry as two Connected System Objects.
        string[] objectClasses = ["top", "posixAccount", "inetOrgPerson"];
        ConnectedSystemObjectType[] schema = [_inetOrgPerson, _posixAccount];

        var emittedBy = schema
            .Where(searched => LdapObjectTypeMatcher.Match(objectClasses, schema) is { } matched &&
                               LdapObjectTypeMatcher.OwnsEntry(matched, searched))
            .ToList();

        Assert.That(emittedBy, Has.Count.EqualTo(1));
        Assert.That(emittedBy[0], Is.SameAs(_inetOrgPerson));
    }

    [Test]
    public void MatchAndOwnsEntry_ObjectClassOrderReversed_TheSameSearchStillEmitsIt()
    {
        ConnectedSystemObjectType[] schema = [_inetOrgPerson, _posixAccount];

        var forwards = schema.Where(searched =>
            LdapObjectTypeMatcher.Match(["posixAccount", "inetOrgPerson"], schema) is { } matched &&
            LdapObjectTypeMatcher.OwnsEntry(matched, searched)).ToList();

        var backwards = schema.Where(searched =>
            LdapObjectTypeMatcher.Match(["inetOrgPerson", "posixAccount"], schema) is { } matched &&
            LdapObjectTypeMatcher.OwnsEntry(matched, searched)).ToList();

        Assert.That(forwards, Is.EqualTo(backwards));
        Assert.That(forwards, Has.Count.EqualTo(1));
    }

    #endregion

    #region Helpers

    private static ConnectedSystemObjectType StructuralType(string name) =>
        TypeOfKind(name, ObjectTypeTags.Values.ClassKindStructural);

    private static ConnectedSystemObjectType AuxiliaryType(string name) =>
        TypeOfKind(name, ObjectTypeTags.Values.ClassKindAuxiliary);

    private static ConnectedSystemObjectType TypeOfKind(string name, string classKind) => new()
    {
        Name = name,
        Selected = true,
        Tags = [new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = classKind }]
    };

    #endregion
}
