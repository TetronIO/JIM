// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// A Connected System Object loaded for export with only the members its changes name (#2816): what it holds is read
/// through <see cref="ConnectedSystemObject.HeldValueCount"/>, <see cref="ConnectedSystemObject.HoldsAnyValue"/> and
/// <see cref="ConnectedSystemObject.HeldValuesOf"/>, which answer from the loaded values exactly as before on an object
/// loaded whole, and from the held count where an attribute was loaded in part. Nothing that needs every value of such an
/// attribute may read it as though it were all there.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectPartialLoadTests
{
    private static readonly ConnectedSystemObjectTypeAttribute Member = new()
    {
        Id = 2, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };

    private static readonly ConnectedSystemObjectTypeAttribute DistinguishedName = new()
    {
        Id = 1, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true
    };

    private static ConnectedSystemObject GroupWithMembers(int memberCount)
    {
        var group = new ConnectedSystemObject { Id = Guid.NewGuid(), SecondaryExternalIdAttributeId = DistinguishedName.Id };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = DistinguishedName, AttributeId = DistinguishedName.Id, StringValue = "cn=Staff,ou=groups"
        });
        for (var i = 0; i < memberCount; i++)
        {
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), Attribute = Member, AttributeId = Member.Id, UnresolvedReferenceValue = $"cn=user{i},ou=people"
            });
        }
        return group;
    }

    private static ConnectedSystemObject MarkMembersLoadedInPart(ConnectedSystemObject group, int atLeast)
    {
        group.PartiallyLoadedAttributes = new Dictionary<int, PartiallyLoadedAttribute>
        {
            [Member.Id] = new ExportPartiallyLoadedAttribute(Member.Id, Member.Name, atLeast)
        };
        return group;
    }

    [Test]
    public void HeldValueCount_WhollyLoaded_CountsTheLoadedValuesByAttributeNameIgnoringCase()
    {
        var group = GroupWithMembers(3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.HeldValueCount("MEMBER"), Is.EqualTo(3));
            Assert.That(group.HeldValueCount("uniqueMember"), Is.Zero);
            Assert.That(group.IsPartiallyLoaded, Is.False);
        }
    }

    [Test]
    public void HeldValueCount_PartiallyLoadedAttribute_AnswersFromTheHeldCountNotTheLoadedValues()
    {
        var group = MarkMembersLoadedInPart(GroupWithMembers(1), atLeast: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.HeldValueCount("member"), Is.EqualTo(5), "one member loaded, at least five held");
            Assert.That(group.IsPartiallyLoaded, Is.True);
            Assert.That(group.IsAttributeWhollyLoaded(Member.Id), Is.False);
            Assert.That(group.IsAttributeWhollyLoaded(DistinguishedName.Id), Is.True);
        }
    }

    [Test]
    public void HoldsAnyValue_PartiallyLoadedAttributeWithNoValueLoaded_ReturnsTrue()
    {
        var group = MarkMembersLoadedInPart(GroupWithMembers(0), atLeast: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.HoldsAnyValue("member"), Is.True, "a missing value never reads as not held when it was not loaded");
            Assert.That(group.HoldsAnyValue("distinguishedName"), Is.True);
            Assert.That(group.HoldsAnyValue("description"), Is.False);
        }
    }

    [Test]
    public void HeldValuesOf_WhollyLoaded_ReturnsTheAttributesValues()
    {
        var group = GroupWithMembers(2);

        Assert.That(group.HeldValuesOf("member").Select(v => v.UnresolvedReferenceValue),
            Is.EquivalentTo(new[] { "cn=user0,ou=people", "cn=user1,ou=people" }));
    }

    [Test]
    public void HeldValuesOf_PartiallyLoadedAttribute_Throws()
    {
        var group = MarkMembersLoadedInPart(GroupWithMembers(1), atLeast: 3);

        Assert.That(() => group.HeldValuesOf("member"), Throws.TypeOf<PartiallyLoadedObjectException>());
    }

    [Test]
    public void EnsureWhollyLoaded_PartiallyLoaded_ThrowsNamingTheOperationAndTheAttribute()
    {
        var group = MarkMembersLoadedInPart(GroupWithMembers(1), atLeast: 3);

        Assert.That(() => ConnectedSystemObject.EnsureWhollyLoaded([group], "UpdateConnectedSystemObjectAsync"),
            Throws.TypeOf<PartiallyLoadedObjectException>()
                .With.Message.Contains("UpdateConnectedSystemObjectAsync")
                .And.Message.Contains("member"));
    }

    private static ConnectedSystemObject LoadedInPartByAnImport(ConnectedSystemObject group, string[] imported, string[] unmatchedIncoming,
        params Guid[] undecided)
    {
        var answer = new HeldReferenceValueAnswer(new HeldReferenceValueQuestion(group.Id, Member.Id, imported));
        foreach (var id in undecided)
            answer.UnmatchedValueIds.Add(id);
        foreach (var value in unmatchedIncoming)
            answer.UnmatchedIncomingValues.Add(value);
        group.PartiallyLoadedAttributes = new Dictionary<int, PartiallyLoadedAttribute>
        {
            [Member.Id] = new ImportPartiallyLoadedAttribute(Member.Id, Member.Name, answer)
        };
        return group;
    }

    [Test]
    public void HoldsAnyValue_LoadedInPartByAnImport_AnswersFromWhatTheDatabaseMatched()
    {
        // Members the import carries that a held value matched exactly were not loaded, and are held.
        var matched = LoadedInPartByAnImport(GroupWithMembers(0), ["cn=a", "cn=b"], unmatchedIncoming: ["cn=b"]);
        // Nothing carried was held and nothing held was left over: the group holds no member.
        var empty = LoadedInPartByAnImport(GroupWithMembers(0), ["cn=a"], unmatchedIncoming: ["cn=a"]);
        // A held member no carried value matched was loaded.
        var leftOver = LoadedInPartByAnImport(GroupWithMembers(1), [], unmatchedIncoming: [], Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(matched.HoldsAnyValue("member"), Is.True);
            Assert.That(empty.HoldsAnyValue("member"), Is.False);
            Assert.That(leftOver.HoldsAnyValue("member"), Is.True);
            Assert.That(matched.IsPartiallyLoaded, Is.True);
        }
    }

    [Test]
    public void HeldValueCount_LoadedInPartByAnImport_ThrowsRatherThanCountTheLoadedValues()
    {
        var group = LoadedInPartByAnImport(GroupWithMembers(1), ["cn=a"], unmatchedIncoming: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => group.HeldValueCount("member"), Throws.TypeOf<PartiallyLoadedObjectException>().With.Message.Contains("not known"));
            Assert.That(group.HeldValueCount("distinguishedName"), Is.EqualTo(1), "an attribute loaded whole still counts");
        }
    }

    [Test]
    public void EnsureWhollyLoaded_LoadedInPartByAnImport_Throws()
    {
        var group = LoadedInPartByAnImport(GroupWithMembers(1), ["cn=a"], unmatchedIncoming: []);

        Assert.That(() => ConnectedSystemObject.EnsureWhollyLoaded([group], "CreateConnectedSystemObjectsAsync"),
            Throws.TypeOf<PartiallyLoadedObjectException>().With.Message.Contains("Delta Import"));
    }

    [Test]
    public void EnsureWhollyLoaded_WhollyLoaded_DoesNotThrow()
    {
        var group = GroupWithMembers(2);

        Assert.That(() => ConnectedSystemObject.EnsureWhollyLoaded([group], "UpdateConnectedSystemObjectAsync"), Throws.Nothing);
    }
}
