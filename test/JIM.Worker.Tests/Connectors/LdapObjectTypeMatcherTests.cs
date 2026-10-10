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

    // Windows Server and Samba AD both list an entry's classes from top down to the most specific (seen on a Windows
    // Server 2025 domain controller in the Active Directory lab, #2043, and in a Samba AD domain's sam.ldb).
    private static readonly string[] ActiveDirectoryUserClasses = ["top", "person", "organizationalPerson", "user"];
    private static readonly string[] ActiveDirectoryComputerClasses = ["top", "person", "organizationalPerson", "user", "computer"];

    [Test]
    public void Match_ActiveDirectoryUserWithUserAndPersonSelected_ResolvesToUser()
    {
        var user = StructuralType("user");

        var matched = LdapObjectTypeMatcher.Match(ActiveDirectoryUserClasses, [user, _person]);

        Assert.That(matched, Is.SameAs(user), "An Active Directory user must not import as person, the class it inherits from.");
    }

    [Test]
    public void Match_ActiveDirectoryComputerWithUserAndComputerSelected_ResolvesToComputer()
    {
        // computer inherits from user, so a search for user returns every computer too.
        var user = StructuralType("user");
        var computer = StructuralType("computer");

        var matched = LdapObjectTypeMatcher.Match(ActiveDirectoryComputerClasses, [user, computer]);

        Assert.That(matched, Is.SameAs(computer));
    }

    [Test]
    public void Match_ActiveDirectoryUserWithTypesTheSchemaHasNotClassified_ResolvesToUser()
    {
        // Object Types discovered before JIM recorded either a class kind or inheritance still resolve by the order
        // the directory lists the classes in.
        var user = new ConnectedSystemObjectType { Name = "user", Selected = true };
        var person = new ConnectedSystemObjectType { Name = "person", Selected = true };

        var matched = LdapObjectTypeMatcher.Match(ActiveDirectoryUserClasses, [user, person]);

        Assert.That(matched, Is.SameAs(user));
    }

    [Test]
    public void Match_UntaggedTypesWithTopListedLast_ResolvesToTheFirstListed()
    {
        // An RFC 4512 directory returns objectClass in the order it was written, and LDIF commonly lists the most
        // specific class first with top last (JIM's own OpenLDAP lab does). top marks the general end of the list.
        var inetOrgPerson = StructuralType("inetOrgPerson");

        var matched = LdapObjectTypeMatcher.Match(["inetOrgPerson", "organizationalPerson", "person", "top"], [_person, inetOrgPerson]);

        Assert.That(matched, Is.SameAs(inetOrgPerson));
    }

    [Test]
    public void Match_UntaggedTypesWithTopListedFirst_ResolvesToTheLastListed()
    {
        var inetOrgPerson = StructuralType("inetOrgPerson");

        var matched = LdapObjectTypeMatcher.Match(["top", "person", "organizationalPerson", "inetOrgPerson"], [_person, inetOrgPerson]);

        Assert.That(matched, Is.SameAs(inetOrgPerson));
    }

    [Test]
    public void Match_UntaggedTypesWithNoTopListed_KeepsTheFirstListed()
    {
        // Nothing says which end is the general one, so the rule JIM has always applied stands.
        var inetOrgPerson = StructuralType("inetOrgPerson");

        var matched = LdapObjectTypeMatcher.Match(["inetOrgPerson", "person"], [_person, inetOrgPerson]);

        Assert.That(matched, Is.SameAs(inetOrgPerson));
    }

    [Test]
    public void Match_UntaggedTypesWithTopListedBetweenThem_KeepsTheFirstListed()
    {
        var inetOrgPerson = StructuralType("inetOrgPerson");

        var matched = LdapObjectTypeMatcher.Match(["inetOrgPerson", "top", "person"], [_person, inetOrgPerson]);

        Assert.That(matched, Is.SameAs(inetOrgPerson));
    }

    [Test]
    public void Match_InheritanceKnown_ResolvesToTheMostSpecificClassWhateverOrderTheDirectoryListsThemIn()
    {
        var user = StructuralType("user", inheritsFrom: ["organizationalPerson", "person", "top"]);
        var person = StructuralType("person", inheritsFrom: ["top"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LdapObjectTypeMatcher.Match(ActiveDirectoryUserClasses, [user, person]), Is.SameAs(user), "top first");
            Assert.That(LdapObjectTypeMatcher.Match(ActiveDirectoryUserClasses.Reverse(), [user, person]), Is.SameAs(user), "most specific first");
            Assert.That(LdapObjectTypeMatcher.Match(["person", "user", "top", "organizationalPerson"], [user, person]), Is.SameAs(user), "no order at all");
        }
    }

    [Test]
    public void Match_InheritanceKnownForOnlyTheMoreSpecificType_StillResolvesToIt()
    {
        // The more specific class names what it inherits from; the other need say nothing.
        var inetOrgPerson = StructuralType("inetOrgPerson", inheritsFrom: ["organizationalPerson", "person", "top"]);
        var person = StructuralType("person");

        var matched = LdapObjectTypeMatcher.Match(["inetOrgPerson", "person", "top"], [inetOrgPerson, person]);

        Assert.That(matched, Is.SameAs(inetOrgPerson));
    }

    [Test]
    public void Match_InheritanceIsCaseInsensitive()
    {
        var user = StructuralType("user", inheritsFrom: ["ORGANIZATIONALPERSON", "Person", "top"]);
        var person = StructuralType("person");

        var matched = LdapObjectTypeMatcher.Match(["user", "person"], [user, person]);

        Assert.That(matched, Is.SameAs(user));
    }

    [Test]
    public void Match_AuxiliaryClassListedLastAfterTwoStructuralClasses_StillResolvesToTheMostSpecificStructuralClass()
    {
        var inetOrgPerson = StructuralType("inetOrgPerson");

        var matched = LdapObjectTypeMatcher.Match(
            ["top", "person", "organizationalPerson", "inetOrgPerson", "posixAccount"],
            [_person, inetOrgPerson, _posixAccount]);

        Assert.That(matched, Is.SameAs(inetOrgPerson));
    }

    [Test]
    public void Match_UnclassifiedTypesAreNotTreatedAsAuxiliary()
    {
        // An Object Type discovered before JIM classified class kinds must behave as it always has.
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

    private static ConnectedSystemObjectType StructuralType(string name, string[]? inheritsFrom = null)
    {
        var objectType = TypeOfKind(name, ObjectTypeTags.Values.ClassKindStructural);
        foreach (var superiorClass in inheritsFrom ?? [])
            objectType.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.SuperiorClass, Value = superiorClass });
        return objectType;
    }

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
