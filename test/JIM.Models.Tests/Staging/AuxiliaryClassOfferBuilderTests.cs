// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// What the portal, the REST API and PowerShell all offer an administrator when they ask which auxiliary classes
/// can be merged into a structural Object Type.
/// </summary>
[TestFixture]
public class AuxiliaryClassOfferBuilderTests
{
    private ConnectedSystemObjectType _inetOrgPerson = null!;
    private ConnectedSystemObjectType _posixAccount = null!;
    private ConnectedSystemObjectType _shadowAccount = null!;
    private ConnectedSystemObjectType _sambaSamAccount = null!;

    [SetUp]
    public void SetUp()
    {
        _inetOrgPerson = StructuralType(1, "inetOrgPerson");
        _inetOrgPerson.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            Key = ObjectTypeTags.Keys.ClassMembershipAttribute,
            Value = "objectClass"
        });

        _posixAccount = AuxiliaryType(2, "posixAccount");
        _shadowAccount = AuxiliaryType(3, "shadowAccount");
        _sambaSamAccount = AuxiliaryType(4, "sambaSamAccount");
    }

    #region Build

    [Test]
    public void Build_ObjectTypeThatDoesNotManageClassMembership_OffersNothing()
    {
        // An Active Directory Object Type carries no class membership attribute, so there is nothing to merge into.
        _inetOrgPerson.Tags.RemoveAll(tag => tag.Key == ObjectTypeTags.Keys.ClassMembershipAttribute);

        var offers = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes());

        Assert.That(offers, Is.Empty);
    }

    [Test]
    public void Build_AnAuxiliaryObjectType_OffersNothing()
    {
        _posixAccount.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            Key = ObjectTypeTags.Keys.ClassMembershipAttribute,
            Value = "objectClass"
        });

        var offers = AuxiliaryClassOfferBuilder.Build(_posixAccount, AllTypes());

        Assert.That(offers, Is.Empty);
    }

    [Test]
    public void Build_OffersEveryAuxiliaryClassInTheSchemaAndNoStructuralOne()
    {
        var offers = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes());

        Assert.That(offers.Select(offer => offer.ObjectType.Name),
            Is.EquivalentTo(new[] { "posixAccount", "shadowAccount", "sambaSamAccount" }));
    }

    [Test]
    public void Build_AClassAlreadyMerged_IsMarkedMergedAndListedFirst()
    {
        _inetOrgPerson.Extensions.Add(new ConnectedSystemObjectTypeExtension
        {
            BaseObjectTypeId = _inetOrgPerson.Id,
            ExtensionObjectTypeId = _sambaSamAccount.Id
        });

        var offers = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(offers[0].ObjectType.Name, Is.EqualTo("sambaSamAccount"));
            Assert.That(offers[0].Merged, Is.True);
            Assert.That(offers.Skip(1).Any(offer => offer.Merged), Is.False);
        }
    }

    [Test]
    public void Build_AClassADitContentRulePermits_IsMarkedSuggestedAndOrderedAboveAnUnsuggestedOne()
    {
        _inetOrgPerson.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            Key = ObjectTypeTags.Keys.PermittedAuxiliaryClass,
            Value = "shadowAccount"
        });

        var offers = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(offers[0].ObjectType.Name, Is.EqualTo("shadowAccount"));
            Assert.That(offers[0].PermittedByTheConnectedSystem, Is.True);
            Assert.That(offers[0].IsSuggested, Is.True);
            Assert.That(offers[1].IsSuggested, Is.False);
        }
    }

    [Test]
    public void Build_ClassesADiscoveryRunObserved_AreOrderedByHowWidelyTheyAreUsed()
    {
        var run = new AuxiliaryClassDiscoveryRun
        {
            ConnectedSystemId = 1,
            Status = AuxiliaryClassDiscoveryStatus.Complete,
            Results =
            [
                Observation(_inetOrgPerson.Id, "sambaSamAccount", 12),
                Observation(_inetOrgPerson.Id, "posixAccount", 1204)
            ]
        };

        var offers = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes(), run);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(offers[0].ObjectType.Name, Is.EqualTo("posixAccount"));
            Assert.That(offers[0].EntriesObservedOn, Is.EqualTo(1204));
            Assert.That(offers[1].ObjectType.Name, Is.EqualTo("sambaSamAccount"));
            Assert.That(offers[2].ObjectType.Name, Is.EqualTo("shadowAccount"));
            Assert.That(offers[2].EntriesObservedOn, Is.Null);
        }
    }

    [Test]
    public void Build_ObservationsAgainstAnotherObjectType_AreNotAttributedToThisOne()
    {
        var otherStructuralType = StructuralType(9, "groupOfNames");
        var run = new AuxiliaryClassDiscoveryRun
        {
            Results = [Observation(otherStructuralType.Id, "posixAccount", 500)]
        };

        var offers = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes(), run);

        Assert.That(offers.Single(offer => offer.ObjectType.Name == "posixAccount").EntriesObservedOn, Is.Null);
    }

    [Test]
    public void Build_NothingMergedOrSuggested_OrdersByNameSoAKnownClassCanBeFound()
    {
        var offers = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes());

        Assert.That(offers.Select(offer => offer.ObjectType.Name),
            Is.EqualTo(new[] { "posixAccount", "sambaSamAccount", "shadowAccount" }));
    }

    [Test]
    public void Build_ReportsWhatEachClassWouldContribute()
    {
        _posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Name = "uidNumber" });
        _posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Name = "homeDirectory" });

        var offers = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes());

        Assert.That(offers.Single(offer => offer.ObjectType.Name == "posixAccount").ContributedAttributeCount,
            Is.EqualTo(2));
    }

    /// <summary>
    /// The Connector gives every class the directory's common attributes (the entry's identity and class
    /// membership, say), so an auxiliary class's own attribute list overlaps the structural class's. Counting
    /// those as a contribution overstates every class by the same three or four, and listing them promises
    /// attributes the Object Type already has. What merging contributes is what the Object Type lacks.
    /// </summary>
    [Test]
    public void Build_WhatAClassContributes_ExcludesAttributesTheObjectTypeAlreadyCarries()
    {
        _inetOrgPerson.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "entryUUID" });
        _inetOrgPerson.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "objectClass" });
        _posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "entryUUID" });
        _posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "ObjectClass" });
        _posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "uidNumber" });
        _posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "gidNumber" });

        var offer = AuxiliaryClassOfferBuilder.Build(_inetOrgPerson, AllTypes())
            .Single(candidate => candidate.ObjectType.Name == "posixAccount");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(offer.ContributedAttributeCount, Is.EqualTo(2));
            Assert.That(offer.ContributedAttributes.Select(attribute => attribute.Name), Is.EqualTo(new[] { "gidNumber", "uidNumber" }),
                "ordered by name, so every surface lists them the same way");
        }
    }

    #endregion

    #region CarrierCandidates

    [Test]
    public void CarrierCandidates_OffersOnlyStructuralClasses()
    {
        var abstractType = new ConnectedSystemObjectType { Id = 8, Name = "top" };
        abstractType.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            Key = ObjectTypeTags.Keys.ClassKind,
            Value = ObjectTypeTags.Values.ClassKindAbstract
        });
        var unclassified = new ConnectedSystemObjectType { Id = 9, Name = "device" };

        var candidates = AuxiliaryClassOfferBuilder.CarrierCandidates(
            [.. AllTypes(), abstractType, unclassified, StructuralType(10, "account")]);

        Assert.That(candidates.Select(candidate => candidate.Name), Is.EqualTo(new[] { "account", "inetOrgPerson" }));
    }

    #endregion

    #region Helpers

    private List<ConnectedSystemObjectType> AllTypes() =>
        [_inetOrgPerson, _posixAccount, _shadowAccount, _sambaSamAccount];

    private static AuxiliaryClassDiscoveryResult Observation(int structuralObjectTypeId, string auxiliaryClassName, int entryCount) =>
        new()
        {
            StructuralObjectTypeId = structuralObjectTypeId,
            AuxiliaryClassName = auxiliaryClassName,
            EntryCount = entryCount
        };

    private static ConnectedSystemObjectType StructuralType(int id, string name) =>
        TypeOfKind(id, name, ObjectTypeTags.Values.ClassKindStructural);

    private static ConnectedSystemObjectType AuxiliaryType(int id, string name) =>
        TypeOfKind(id, name, ObjectTypeTags.Values.ClassKindAuxiliary);

    private static ConnectedSystemObjectType TypeOfKind(int id, string name, string classKind)
    {
        var objectType = new ConnectedSystemObjectType { Id = id, Name = name, Selected = true };
        objectType.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = classKind });
        return objectType;
    }

    #endregion
}
