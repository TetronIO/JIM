// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for issue #1386: a Connected System Object holding two attribute values for its External ID
/// attribute must error that one object on its Run Profile Execution Item, never abort the whole run.
/// Before the fix, <see cref="ConnectedSystemObject.ExternalIdAttributeValue"/>'s SingleOrDefault threw
/// out of the change-record path, SafeFailActivityAsync failed the entire Activity, and every object
/// still to be processed was abandoned; nothing recorded which object was at fault.
/// </summary>
[TestFixture]
public class CsoChangeRecordExternalIdGuardTests
{
    private JimApplication Jim { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute ExternalIdAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute UserNameAttribute { get; set; } = null!;
    private ConnectedSystemObjectType ObjectType { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        Jim = new JimApplication(new PostgresDataRepository(new Mock<JimDbContext>().Object));

        ExternalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 100,
            Name = "ID",
            Type = AttributeDataType.Number
        };
        UserNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 101,
            Name = "USER_NAME",
            Type = AttributeDataType.Text
        };
        ObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "AppUser",
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { ExternalIdAttribute, UserNameAttribute }
        };
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    /// <summary>
    /// Builds a CSO of <see cref="ObjectType"/> with a pending attribute value addition, so the
    /// change-record path has work to do. <paramref name="duplicateExternalId"/> gives the object a
    /// second value for its External ID attribute: the corrupt state observed in #1386, where the
    /// export confirm had stored the database-generated anchor as a string and the confirming import,
    /// unable to see it in the typed diff, staged a typed duplicate alongside it.
    /// </summary>
    private ConnectedSystemObject CreateCso(bool duplicateExternalId)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Type = ObjectType,
            TypeId = ObjectType.Id,
            ExternalIdAttributeId = ExternalIdAttribute.Id,
            Status = ConnectedSystemObjectStatus.Normal
        };

        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = ExternalIdAttribute,
            AttributeId = ExternalIdAttribute.Id,
            StringValue = "1000039"
        });

        if (duplicateExternalId)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = cso,
                Attribute = ExternalIdAttribute,
                AttributeId = ExternalIdAttribute.Id,
                IntValue = 1000039
            });
        }

        cso.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            Attribute = UserNameAttribute,
            AttributeId = UserNameAttribute.Id,
            StringValue = "S00000001"
        });

        return cso;
    }

    private static ActivityRunProfileExecutionItem CreateRpei(ConnectedSystemObject cso) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemObject = cso
    };

    [Test]
    public void LinkUpdateChangeRecords_DuplicateExternalIdValues_ErrorsTheObjectAndDoesNotThrow()
    {
        var corruptCso = CreateCso(duplicateExternalId: true);
        var rpei = CreateRpei(corruptCso);

        Assert.That(
            () => Jim.ConnectedSystems.LinkUpdateChangeRecords(
                new List<ConnectedSystemObject> { corruptCso },
                new List<ActivityRunProfileExecutionItem> { rpei },
                changeTrackingEnabled: true),
            Throws.Nothing,
            "A corrupt object must be errored on its RPEI, never thrown out of the update path.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError),
                "The object must be errored on its Run Profile Execution Item.");
            Assert.That(rpei.ErrorMessage, Does.Contain(corruptCso.Id.ToString()),
                "The error must name the object at fault.");
            Assert.That(rpei.ErrorMessage, Does.Contain("1000039"),
                "The error must name the colliding External ID values.");
            Assert.That(rpei.ConnectedSystemObjectChange, Is.Null,
                "No change record can be built for an object whose External ID is ambiguous.");
        }
    }

    [Test]
    public void LinkUpdateChangeRecords_DuplicateExternalIdValues_ContinuesWithRemainingObjects()
    {
        var corruptCso = CreateCso(duplicateExternalId: true);
        var healthyCso = CreateCso(duplicateExternalId: false);
        var corruptRpei = CreateRpei(corruptCso);
        var healthyRpei = CreateRpei(healthyCso);

        Assert.That(
            () => Jim.ConnectedSystems.LinkUpdateChangeRecords(
                new List<ConnectedSystemObject> { corruptCso, healthyCso },
                new List<ActivityRunProfileExecutionItem> { corruptRpei, healthyRpei },
                changeTrackingEnabled: true),
            Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corruptRpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError),
                "The corrupt object must be errored.");
            Assert.That(healthyRpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet).Or.Null,
                "The healthy object must not be errored.");
            Assert.That(healthyRpei.ConnectedSystemObjectChange, Is.Not.Null,
                "Objects after the corrupt one must still get their change records; one bad object must not abandon the batch.");
            Assert.That(healthyRpei.ConnectedSystemObjectChange!.DeletedObjectExternalId, Is.EqualTo("1000039"),
                "The healthy object's change record must carry its External ID as before.");
        }
    }

    [Test]
    public void LinkUpdateChangeRecords_DuplicateExternalIdValues_ClearsPendingListsSoTheObjectIsNotReprocessed()
    {
        var corruptCso = CreateCso(duplicateExternalId: true);
        var rpei = CreateRpei(corruptCso);

        Jim.ConnectedSystems.LinkUpdateChangeRecords(
            new List<ConnectedSystemObject> { corruptCso },
            new List<ActivityRunProfileExecutionItem> { rpei },
            changeTrackingEnabled: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corruptCso.PendingAttributeValueAdditions, Is.Empty,
                "Pending additions were snapshotted for persistence by the caller before this ran; leaving them would make the object look like it still holds unapplied work.");
            Assert.That(corruptCso.PendingAttributeValueRemovals, Is.Empty,
                "Pending removals must be cleared for the same reason.");
        }
    }

    [Test]
    public void LinkDeleteChangeRecords_DuplicateExternalIdValues_ErrorsTheObjectAndStillUnlinksAllRpeis()
    {
        var corruptCso = CreateCso(duplicateExternalId: true);
        var healthyCso = CreateCso(duplicateExternalId: false);
        var corruptRpei = CreateRpei(corruptCso);
        var healthyRpei = CreateRpei(healthyCso);

        Assert.That(
            () => Jim.ConnectedSystems.LinkDeleteChangeRecords(
                new List<ConnectedSystemObject> { corruptCso, healthyCso },
                new List<ActivityRunProfileExecutionItem> { corruptRpei, healthyRpei },
                changeTrackingEnabled: true),
            Throws.Nothing,
            "The delete path reads the same External ID property and must be guarded the same way.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corruptRpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError),
                "The corrupt object must be errored.");
            Assert.That(healthyRpei.ConnectedSystemObjectChange, Is.Not.Null,
                "The healthy object must still get its deletion change record.");
            Assert.That(corruptRpei.ConnectedSystemObjectId, Is.Null,
                "The CSO FK must be nulled on every RPEI, errored or not: the CSOs are about to be deleted and a live FK would violate the constraint at persistence.");
            Assert.That(healthyRpei.ConnectedSystemObjectId, Is.Null,
                "The CSO FK must be nulled on every RPEI.");
        }
    }
}
