// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;
using System.Linq;

namespace JIM.Models.Tests;

/// <summary>
/// Hiding an object type is the one thing this classification does that a user can see, so the question "is this
/// internal?" is answered in one place and pinned here. Getting it wrong in the permissive direction shows noise;
/// getting it wrong in the other direction hides a class an administrator needs, which is far worse.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectTypeTagExtensionsTests
{
    [Test]
    public void IsInternal_WhenTheTypeCarriesTheInternalVisibilityTag_IsTrue()
    {
        var objectType = ObjectTypeWithTags((ObjectTypeTags.Keys.Visibility, ObjectTypeTags.Values.VisibilityInternal));

        Assert.That(objectType.IsInternal(), Is.True);
    }

    [Test]
    public void IsInternal_WhenTheTypeCarriesNoTagsAtAll_IsFalse()
    {
        // An unclassified object type must always be shown. Connectors that do not classify visibility (the File and
        // SCIM connectors today) rely on this.
        Assert.That(ObjectTypeWithTags().IsInternal(), Is.False);
    }

    [Test]
    public void IsInternal_WhenTheTypeIsClassifiedOnlyByClassKind_IsFalse()
    {
        var objectType = ObjectTypeWithTags((ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural));

        Assert.That(objectType.IsInternal(), Is.False);
    }

    [Test]
    public void IsInternal_WhenTheTypeIsExplicitlyStandard_IsFalse()
    {
        var objectType = ObjectTypeWithTags((ObjectTypeTags.Keys.Visibility, ObjectTypeTags.Values.VisibilityStandard));

        Assert.That(objectType.IsInternal(), Is.False);
    }

    [Test]
    public void IsInternal_WhenAnotherKeyCarriesTheInternalValue_IsFalse()
    {
        // A connector may add keys of its own alongside JIM's. The value alone must not decide the answer.
        var objectType = ObjectTypeWithTags(("some-connector-key", ObjectTypeTags.Values.VisibilityInternal));

        Assert.That(objectType.IsInternal(), Is.False);
    }

    [Test]
    public void IsInternal_WhenTheInternalTagSitsAlongsideOthers_IsTrue()
    {
        var objectType = ObjectTypeWithTags(
            (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural),
            (ObjectTypeTags.Keys.Visibility, ObjectTypeTags.Values.VisibilityInternal));

        Assert.That(objectType.IsInternal(), Is.True);
    }

    #region TakesEntriesFrom

    // #2049: which of two selected Object Types an entry carrying both classes imports as. The LDAP Connector's
    // matcher and the schema selection preview both ask this, so a preview can never promise one Object Type while
    // the import resolves the entry to the other.

    [Test]
    public void TakesEntriesFrom_StructuralTypeAgainstAnAuxiliaryOne_IsTrue()
    {
        var inetOrgPerson = NamedTypeWithTags("inetOrgPerson", (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural));
        var posixAccount = NamedTypeWithTags("posixAccount", (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inetOrgPerson.TakesEntriesFrom(posixAccount), Is.True,
                "a structural class is what an entry is; an auxiliary class is only something it also carries");
            Assert.That(posixAccount.TakesEntriesFrom(inetOrgPerson), Is.False);
        }
    }

    [Test]
    public void TakesEntriesFrom_UnclassifiedTypeAgainstAnAuxiliaryOne_IsTrue()
    {
        // An Object Type discovered before JIM recorded class kinds is not auxiliary, so it is treated as what the
        // entry is, exactly as the import has always treated it.
        var unclassified = NamedTypeWithTags("user");
        var posixAccount = NamedTypeWithTags("posixAccount", (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unclassified.TakesEntriesFrom(posixAccount), Is.True);
            Assert.That(posixAccount.TakesEntriesFrom(unclassified), Is.False);
        }
    }

    [Test]
    public void TakesEntriesFrom_TwoAuxiliaryTypes_IsFalseEitherWay()
    {
        // Nothing in the schema ranks two auxiliary classes; the order the entry lists them in decides.
        var posixAccount = NamedTypeWithTags("posixAccount", (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary));
        var shadowAccount = NamedTypeWithTags("shadowAccount", (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(posixAccount.TakesEntriesFrom(shadowAccount), Is.False);
            Assert.That(shadowAccount.TakesEntriesFrom(posixAccount), Is.False);
        }
    }

    [Test]
    public void TakesEntriesFrom_MoreSpecificStructuralTypeAgainstTheClassItInheritsFrom_IsTrue()
    {
        var computer = NamedTypeWithTags("computer",
            (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural),
            (ObjectTypeTags.Keys.SuperiorClass, "user"),
            (ObjectTypeTags.Keys.SuperiorClass, "person"));
        var user = NamedTypeWithTags("user",
            (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural),
            (ObjectTypeTags.Keys.SuperiorClass, "person"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(computer.TakesEntriesFrom(user), Is.True, "an entry carrying both is the more specific class");
            Assert.That(user.TakesEntriesFrom(computer), Is.False);
        }
    }

    [Test]
    public void TakesEntriesFrom_InheritanceNamesTheOtherTypeInADifferentCase_IsTrue()
    {
        var computer = NamedTypeWithTags("computer", (ObjectTypeTags.Keys.SuperiorClass, "USER"));
        var user = NamedTypeWithTags("user");

        Assert.That(computer.TakesEntriesFrom(user), Is.True);
    }

    [Test]
    public void TakesEntriesFrom_StructuralTypesWithNoInheritanceRecorded_IsFalseEitherWay()
    {
        // Object Types discovered before JIM recorded inheritance. The schema says nothing, so the order the entry
        // lists its classes in decides, and that cannot be known without the entry.
        var user = NamedTypeWithTags("user", (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural));
        var person = NamedTypeWithTags("person", (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.TakesEntriesFrom(person), Is.False);
            Assert.That(person.TakesEntriesFrom(user), Is.False);
        }
    }

    [Test]
    public void TakesEntriesFrom_AuxiliaryTypeNamingAStructuralOneAsItsSuperior_IsFalse()
    {
        // Class kind outranks inheritance: an auxiliary class never takes an entry from a structural one, whatever
        // its recorded inheritance says.
        var auxiliary = NamedTypeWithTags("extendedPerson",
            (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary),
            (ObjectTypeTags.Keys.SuperiorClass, "person"));
        var person = NamedTypeWithTags("person", (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(auxiliary.TakesEntriesFrom(person), Is.False);
            Assert.That(person.TakesEntriesFrom(auxiliary), Is.True);
        }
    }

    #endregion

    private static ConnectedSystemObjectType ObjectTypeWithTags(params (string Key, string Value)[] tags)
    {
        return NamedTypeWithTags("testObjectClass", tags);
    }

    private static ConnectedSystemObjectType NamedTypeWithTags(string name, params (string Key, string Value)[] tags)
    {
        return new ConnectedSystemObjectType
        {
            Name = name,
            Tags = tags.Select(tag => new ConnectedSystemObjectTypeTag { Key = tag.Key, Value = tag.Value }).ToList()
        };
    }
}
