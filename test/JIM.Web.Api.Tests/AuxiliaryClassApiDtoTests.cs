// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST representation of auxiliary class configuration. An automation author works from ids rather than names,
/// so what is merged, what may carry an auxiliary Object Type, and whether merging means anything for a given
/// Connected System all have to be readable without matching classification tag strings by hand.
/// </summary>
[TestFixture]
public class AuxiliaryClassApiDtoTests
{
    #region ConnectedSystemObjectTypeDto

    [Test]
    public void FromEntity_CarriesTheMergedAuxiliaryClassesAsIds()
    {
        var objectType = StructuralType(1, "inetOrgPerson");
        objectType.Extensions.Add(new ConnectedSystemObjectTypeExtension { BaseObjectTypeId = 1, ExtensionObjectTypeId = 7 });
        objectType.Extensions.Add(new ConnectedSystemObjectTypeExtension { BaseObjectTypeId = 1, ExtensionObjectTypeId = 3 });

        var dto = ConnectedSystemObjectTypeDto.FromEntity(objectType);

        // Ordered, so a caller comparing what it sent against what came back is not tripped by row order.
        Assert.That(dto.MergedAuxiliaryClassObjectTypeIds, Is.EqualTo(new[] { 3, 7 }));
    }

    [Test]
    public void FromEntity_ReportsWhetherMergingMeansAnythingForThisObjectType()
    {
        var withoutClassMembership = StructuralType(1, "user");
        var withClassMembership = StructuralType(2, "inetOrgPerson");
        withClassMembership.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            Key = ObjectTypeTags.Keys.ClassMembershipAttribute,
            Value = "objectClass"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConnectedSystemObjectTypeDto.FromEntity(withoutClassMembership).ManagesClassMembership, Is.False);
            Assert.That(ConnectedSystemObjectTypeDto.FromEntity(withClassMembership).ManagesClassMembership, Is.True);
        }
    }

    [Test]
    public void FromEntity_ReportsAnAuxiliaryObjectTypeAndItsCarrier()
    {
        var posixAccount = new ConnectedSystemObjectType
        {
            Id = 2,
            Name = "posixAccount",
            StructuralCarrierObjectTypeId = 9,
            Tags =
            [
                new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindAuxiliary }
            ]
        };

        var dto = ConnectedSystemObjectTypeDto.FromEntity(posixAccount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.IsAuxiliary, Is.True);
            Assert.That(dto.StructuralCarrierObjectTypeId, Is.EqualTo(9));
        }
    }

    [Test]
    public void FromEntity_ForAnObjectTypeWithNothingMerged_ReportsAnEmptyListNotNull()
    {
        var dto = ConnectedSystemObjectTypeDto.FromEntity(StructuralType(1, "inetOrgPerson"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.MergedAuxiliaryClassObjectTypeIds, Is.Empty);
            Assert.That(dto.StructuralCarrierObjectTypeId, Is.Null);
            Assert.That(dto.IsAuxiliary, Is.False);
        }
    }

    #endregion

    #region AuxiliaryClassOfferDto

    [Test]
    public void AuxiliaryClassOfferDto_FromEntity_CarriesTheOfferAndItsReasons()
    {
        var offer = new AuxiliaryClassOffer
        {
            ObjectType = new ConnectedSystemObjectType { Id = 2, Name = "posixAccount" },
            Merged = true,
            ContributedAttributes = Enumerable.Range(1, 7).Select(i => new ConnectedSystemObjectTypeAttribute { Id = i, Name = $"attribute{i}" }).ToList(),
            PermittedByTheConnectedSystem = true,
            EntriesObservedOn = 1204
        };

        var dto = AuxiliaryClassOfferDto.FromEntity(offer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ObjectTypeId, Is.EqualTo(2));
            Assert.That(dto.Name, Is.EqualTo("posixAccount"));
            Assert.That(dto.Merged, Is.True);
            Assert.That(dto.ContributedAttributeCount, Is.EqualTo(7));
            Assert.That(dto.PermittedByTheConnectedSystem, Is.True);
            Assert.That(dto.EntriesObservedOn, Is.EqualTo(1204));
            Assert.That(dto.IsSuggested, Is.True);
        }
    }

    [Test]
    public void AuxiliaryClassOfferDto_FromEntity_ForAClassNothingSuggests_SaysSoRatherThanReportingZero()
    {
        // Null and zero mean different things: never observed, versus observed on none of the entries read.
        var offer = new AuxiliaryClassOffer
        {
            ObjectType = new ConnectedSystemObjectType { Id = 4, Name = "sambaSamAccount" },
            ContributedAttributes = Enumerable.Range(1, 21).Select(i => new ConnectedSystemObjectTypeAttribute { Id = i, Name = $"attribute{i}" }).ToList()
        };

        var dto = AuxiliaryClassOfferDto.FromEntity(offer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.EntriesObservedOn, Is.Null);
            Assert.That(dto.IsSuggested, Is.False);
        }
    }

    #endregion

    #region AuxiliaryClassDiscoveryRunDto

    /// <summary>
    /// A count alone does not let a caller decide whether to merge a class; the names do. Ordered by name, with
    /// credential attributes marked, because those can never be selected however the class is merged.
    /// </summary>
    [Test]
    public void AuxiliaryClassOfferDto_FromEntity_ListsTheAttributesItWouldContribute()
    {
        var posixAccount = AuxiliaryType(2, "posixAccount");
        posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 21, Name = "uidNumber", Type = AttributeDataType.Number, Required = true });
        posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 22, Name = "userPassword", Type = AttributeDataType.Text });
        posixAccount.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 23, Name = "gecos", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued });

        var dto = AuxiliaryClassOfferDto.FromEntity(new AuxiliaryClassOffer
        {
            ObjectType = posixAccount,
            ContributedAttributes = posixAccount.Attributes.OrderBy(attribute => attribute.Name).ToList()
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ContributedAttributes.Select(a => a.Name), Is.EqualTo(new[] { "gecos", "uidNumber", "userPassword" }));
            Assert.That(dto.ContributedAttributes.Single(a => a.Name == "uidNumber").Required, Is.True);
            Assert.That(dto.ContributedAttributes.Single(a => a.Name == "uidNumber").Type, Is.EqualTo("Number"));
            Assert.That(dto.ContributedAttributes.Single(a => a.Name == "gecos").AttributePlurality, Is.EqualTo("MultiValued"));
            Assert.That(dto.ContributedAttributes.Single(a => a.Name == "userPassword").IsCredential, Is.True);
            Assert.That(dto.ContributedAttributes.Count(a => a.IsCredential), Is.EqualTo(1));
        }
    }

    [Test]
    public void AuxiliaryClassDiscoveryRunDto_FromEntity_CarriesTheRunAndItsResults()
    {
        var activityId = Guid.NewGuid();
        var run = new AuxiliaryClassDiscoveryRun
        {
            Id = 11,
            Scope = AuxiliaryClassDiscoveryScope.QuickSample,
            SampleSizePerObjectType = 5000,
            Status = AuxiliaryClassDiscoveryStatus.Complete,
            Started = new DateTime(2026, 7, 28, 8, 0, 0, DateTimeKind.Utc),
            Completed = new DateTime(2026, 7, 28, 8, 4, 0, DateTimeKind.Utc),
            EntriesRead = 15000,
            ActivityId = activityId,
            InitiatedByName = "Jay",
            Results =
            [
                new AuxiliaryClassDiscoveryResult { StructuralObjectTypeId = 1, AuxiliaryClassName = "posixAccount", EntryCount = 1204 }
            ]
        };

        var dto = AuxiliaryClassDiscoveryRunDto.FromEntity(run);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Id, Is.EqualTo(11));
            Assert.That(dto.Scope, Is.EqualTo(AuxiliaryClassDiscoveryScope.QuickSample));
            Assert.That(dto.SampleSizePerObjectType, Is.EqualTo(5000));
            Assert.That(dto.Status, Is.EqualTo(AuxiliaryClassDiscoveryStatus.Complete));
            Assert.That(dto.EntriesRead, Is.EqualTo(15000));
            Assert.That(dto.ActivityId, Is.EqualTo(activityId));
            Assert.That(dto.InitiatedByName, Is.EqualTo("Jay"));
            Assert.That(dto.Results.Single().AuxiliaryClassName, Is.EqualTo("posixAccount"));
            Assert.That(dto.Results.Single().EntryCount, Is.EqualTo(1204));
        }
    }

    [Test]
    public void AuxiliaryClassDiscoveryRunDto_FromEntity_ForACancelledRun_KeepsWhatItFound()
    {
        // A cancelled run's results are partial but real, so they must survive into the API representation rather
        // than being suppressed as an incomplete answer.
        var run = new AuxiliaryClassDiscoveryRun
        {
            Status = AuxiliaryClassDiscoveryStatus.Cancelled,
            EntriesRead = 412380,
            Results =
            [
                new AuxiliaryClassDiscoveryResult { StructuralObjectTypeId = 1, AuxiliaryClassName = "shadowAccount", EntryCount = 8 }
            ]
        };

        var dto = AuxiliaryClassDiscoveryRunDto.FromEntity(run);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Status, Is.EqualTo(AuxiliaryClassDiscoveryStatus.Cancelled));
            Assert.That(dto.EntriesRead, Is.EqualTo(412380));
            Assert.That(dto.Results, Has.Count.EqualTo(1));
        }
    }

    #endregion

    #region Helpers

    private static ConnectedSystemObjectType StructuralType(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Tags =
        [
            new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural }
        ]
    };

    private static ConnectedSystemObjectType AuxiliaryType(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Tags =
        [
            new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindAuxiliary }
        ]
    };

    #endregion
}
