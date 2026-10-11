// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Reading one Connected System Object's unresolved references with their likely causes (#959): the window the
/// portal, REST and PowerShell list, and the summary above it. The causes themselves are
/// <see cref="UnresolvedReferenceClassifierTests"/>'s subject; these tests cover what the server adds: the
/// Connected System's scope and Connector feeding the classifier, and the ambiguity only the connector space can
/// decide.
/// </summary>
[TestFixture]
public class UnresolvedReferenceDiagnosisServerTests
{
    private const string Corp = "DC=corp,DC=example,DC=com";
    private const string Lab = "DC=lab,DC=example,DC=com";
    private static readonly Guid ObjectId = Guid.NewGuid();

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private JimApplication _jim = null!;
    private IConnector _connector = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        (_connector as IDisposable)?.Dispose();
    }

    [Test]
    public async Task GetUnresolvedReferenceContextAsync_ObjectNotInTheConnectedSystem_ReturnsNullAsync()
    {
        UseDirectory();
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectTypeIdAsync(1, ObjectId)).ReturnsAsync((int?)null);

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);

        Assert.That(context, Is.Null);
    }

    [Test]
    public async Task GetUnresolvedReferenceContextAsync_ObjectInTheConnectedSystem_DescribesWhatThePortalShowsBesideItAsync()
    {
        UseDirectory(UnresolvedReferenceHandling.Warn);

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);

        Assert.That(context, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context!.ConnectedSystemName, Is.EqualTo("Corp AD"));
            Assert.That(context!.ObjectTypeName, Is.EqualTo("group"));
            Assert.That(context!.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Warn),
                "The handling shown beside the references is the Connected System's current setting.");
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectUnresolvedReferenceCountAsync_Object_CountsItsUnresolvedValuesWithoutTheContextAsync()
    {
        UseDirectory();
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValueCountAsync(ObjectId)).ReturnsAsync(5);

        var count = await _jim.ConnectedSystems.GetConnectedSystemObjectUnresolvedReferenceCountAsync(ObjectId);

        Assert.That(count, Is.EqualTo(5));
        // The cheap question a page asks before deciding whether the context is worth building.
        _connectedSystems.Verify(r => r.GetConnectedSystemAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task DiagnoseUnresolvedReferencesAsync_ValuesFromAnyRead_AreDiagnosedInOrderAsTheWindowWouldAsync()
    {
        UseAnchoredSystem();
        AnchorMatches(PersonAnchorId, "42");
        AnchorMatches(ContactAnchorId, "42");

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var diagnoses = await _jim.ConnectedSystems.DiagnoseUnresolvedReferencesAsync(context!,
        [
            new ConnectedSystemObjectUnresolvedReferenceValue { AttributeId = SecretaryAttributeId, AttributeName = "secretary", Value = "42" },
            new ConnectedSystemObjectUnresolvedReferenceValue { AttributeId = ManagerAttributeId, AttributeName = "manager", Value = "42" }
        ]);

        Assert.That(diagnoses.Select(d => (d.AttributeName, d.Cause)), Is.EqualTo(new[]
        {
            ("secretary", UnresolvedReferenceCause.NotFound),
            ("manager", UnresolvedReferenceCause.AmbiguousObjectType)
        }), "The same value is ambiguous only on the attribute that does not declare its target.");
    }

    [Test]
    public async Task GetUnresolvedReferencesRangeAsync_DirectoryObject_DiagnosesEachValueAgainstContainerScopeAsync()
    {
        UseDirectory();
        Window(("member", $"CN=Ana Silva,OU=Contractors,{Corp}"), ("member", $"CN=Lee Grant,OU=Staff,{Corp}"));

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var window = await _jim.ConnectedSystems.GetUnresolvedReferencesRangeAsync(context!, 0, 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalResults, Is.EqualTo(2));
            Assert.That(window.Results.Select(r => r.Cause), Is.EqualTo(new[]
            {
                UnresolvedReferenceCause.ContainerNotInScope,
                UnresolvedReferenceCause.NotFoundInScope
            }));
            Assert.That(window.Results[0].AttributeName, Is.EqualTo("member"));
        }

        // A directory's references resolve by Distinguished Name across every Object Type, so none can be ambiguous
        // and the connector space is never asked.
        _connectedSystems.Verify(r => r.GetConnectedSystemObjectsByAttributeValuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Test]
    public async Task GetUnresolvedReferencesRangeAsync_AnchoredObjectTypesSharingAValue_FlagsItAsAmbiguousAsync()
    {
        UseAnchoredSystem();
        Window(("manager", "42"), ("manager", "77"));
        AnchorMatches(PersonAnchorId, "42");
        AnchorMatches(ContactAnchorId, "42");

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var window = await _jim.ConnectedSystems.GetUnresolvedReferencesRangeAsync(context!, 0, 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Results[0].Cause, Is.EqualTo(UnresolvedReferenceCause.AmbiguousObjectType));
            Assert.That(window.Results[0].CandidateObjectTypeNames, Is.EqualTo(new[] { "contact", "person" }));
            Assert.That(window.Results[1].Cause, Is.EqualTo(UnresolvedReferenceCause.NotFound));
        }
    }

    [Test]
    public async Task GetUnresolvedReferencesRangeAsync_AttributeDeclaringItsTarget_IsNeverCheckedForAmbiguityAsync()
    {
        UseAnchoredSystem();
        Window(("secretary", "42"));

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var window = await _jim.ConnectedSystems.GetUnresolvedReferencesRangeAsync(context!, 0, 50);

        Assert.That(window.Results.Single().Cause, Is.EqualTo(UnresolvedReferenceCause.NotFound));
        _connectedSystems.Verify(r => r.GetConnectedSystemObjectsByAttributeValuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Test]
    public async Task GetUnresolvedReferenceSummaryAsync_DirectoryObject_CountsByCauseAndNamesTheLargestFixAsync()
    {
        UseDirectory();
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValueGroupsAsync(ObjectId)).ReturnsAsync(
        [
            new ConnectedSystemObjectUnresolvedReferenceValueGroup { SampleValue = $"CN=Ana Silva,OU=Contractors,{Corp}", Count = 398 },
            new ConnectedSystemObjectUnresolvedReferenceValueGroup { SampleValue = $"CN=Test,OU=Users,{Lab}", Count = 12 },
            new ConnectedSystemObjectUnresolvedReferenceValueGroup { SampleValue = $"CN=Lee Grant,OU=Staff,{Corp}", Count = 2 }
        ]);

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var summary = await _jim.ConnectedSystems.GetUnresolvedReferenceSummaryAsync(context!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.TotalCount, Is.EqualTo(412));
            Assert.That(summary.Causes.Select(c => (c.Cause, c.Count)), Is.EqualTo(new[]
            {
                (UnresolvedReferenceCause.ContainerNotInScope, 398),
                (UnresolvedReferenceCause.PartitionNotSelected, 12),
                (UnresolvedReferenceCause.NotFoundInScope, 2)
            }));
            Assert.That(summary.LargestFix, Is.Not.Null);
            Assert.That(summary.LargestFix!.Cause, Is.EqualTo(UnresolvedReferenceCause.ContainerNotInScope));
            Assert.That(summary.LargestFix!.ContainerExternalId, Is.EqualTo($"OU=Contractors,{Corp}"));
            Assert.That(summary.LargestFix!.Count, Is.EqualTo(398));
            Assert.That(summary.Fixes.Select(f => (f.Cause, f.FixLocation, f.ContainerExternalId, f.PartitionExternalId, f.Count)), Is.EqualTo(new[]
            {
                (UnresolvedReferenceCause.ContainerNotInScope, UnresolvedReferenceFixLocation.Jim, (string?)$"OU=Contractors,{Corp}", (string?)null, 398),
                (UnresolvedReferenceCause.PartitionNotSelected, UnresolvedReferenceFixLocation.Jim, null, Lab, 12),
                (UnresolvedReferenceCause.NotFoundInScope, UnresolvedReferenceFixLocation.SourceSystem, $"OU=Staff,{Corp}", null, 2)
            }), "Every distinct next step is listed, most values first, wherever its fix lies.");
            Assert.That(summary.Fixes.Select(f => f.Finding), Has.All.Not.Empty);
            Assert.That(summary.Fixes.Select(f => f.Remedy), Has.All.Not.Empty);
            Assert.That(summary.Fixes.Select(f => f.ObjectCount), Has.All.Null, "One object's steps are all about that object.");
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceSummaryAsync_StepsAddressingAsManyValues_ListJimStepsFirstAsync()
    {
        UseDirectory();
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValueGroupsAsync(ObjectId)).ReturnsAsync(
        [
            new ConnectedSystemObjectUnresolvedReferenceValueGroup { SampleValue = $"CN=Lee Grant,OU=Staff,{Corp}", Count = 1 },
            new ConnectedSystemObjectUnresolvedReferenceValueGroup { SampleValue = $"CN=Test,OU=Users,{Lab}", Count = 1 }
        ]);

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var summary = await _jim.ConnectedSystems.GetUnresolvedReferenceSummaryAsync(context!);

        Assert.That(summary.Fixes.Select(f => f.FixLocation), Is.EqualTo(new[]
        {
            UnresolvedReferenceFixLocation.Jim,
            UnresolvedReferenceFixLocation.SourceSystem
        }), "A step the administrator can take in JIM comes before one that lies in the Connected System.");
    }

    [Test]
    public async Task GetUnresolvedReferenceSummaryAsync_GroupsNeedingTheSameChange_AreOneFixAsync()
    {
        UseDirectory();
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValueGroupsAsync(ObjectId)).ReturnsAsync(
        [
            new ConnectedSystemObjectUnresolvedReferenceValueGroup { SampleValue = $"CN=Ana Silva,OU=Contractors,{Corp}", Count = 3 },
            new ConnectedSystemObjectUnresolvedReferenceValueGroup { SampleValue = $"CN=Ben Ortiz,OU=Temps,OU=Contractors,{Corp}", Count = 4 }
        ]);

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var summary = await _jim.ConnectedSystems.GetUnresolvedReferenceSummaryAsync(context!);

        Assert.That(summary.Fixes.Select(f => (f.Cause, f.ContainerExternalId, f.Count)), Is.EqualTo(new[]
        {
            (UnresolvedReferenceCause.ContainerNotInScope, (string?)$"OU=Contractors,{Corp}", 7)
        }), "Two parents beneath one unselected Container need one change, so they are one fix.");
    }

    [Test]
    public async Task GetUnresolvedReferenceSummaryAsync_EveryCauseInTheSourceSystem_HasNoJimFixAsync()
    {
        UseDirectory();
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValueGroupsAsync(ObjectId)).ReturnsAsync(
        [
            new ConnectedSystemObjectUnresolvedReferenceValueGroup { SampleValue = $"CN=Lee Grant,OU=Staff,{Corp}", Count = 2 }
        ]);

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var summary = await _jim.ConnectedSystems.GetUnresolvedReferenceSummaryAsync(context!);

        Assert.That(summary.LargestFix, Is.Null);
    }

    [Test]
    public async Task GetUnresolvedReferenceSummaryAsync_AnchoredObject_SeparatesAmbiguousFromNotFoundAsync()
    {
        UseAnchoredSystem();
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValueCountAsync(ObjectId)).ReturnsAsync(3);
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValuesAsync(ObjectId, It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(ManagerAttributeId) && !ids.Contains(SecretaryAttributeId))))
            .ReturnsAsync(
            [
                new ConnectedSystemObjectUnresolvedReferenceValue { AttributeId = ManagerAttributeId, AttributeName = "manager", Value = "42" },
                new ConnectedSystemObjectUnresolvedReferenceValue { AttributeId = ManagerAttributeId, AttributeName = "manager", Value = "77" }
            ]);
        AnchorMatches(PersonAnchorId, "42");
        AnchorMatches(ContactAnchorId, "42");

        var context = await _jim.ConnectedSystems.GetUnresolvedReferenceContextAsync(1, ObjectId);
        var summary = await _jim.ConnectedSystems.GetUnresolvedReferenceSummaryAsync(context!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.TotalCount, Is.EqualTo(3));
            Assert.That(summary.Causes.Select(c => (c.Cause, c.Count)), Is.EqualTo(new[]
            {
                (UnresolvedReferenceCause.NotFound, 2),
                (UnresolvedReferenceCause.AmbiguousObjectType, 1)
            }));
            Assert.That(summary.LargestFix?.Cause, Is.EqualTo(UnresolvedReferenceCause.AmbiguousObjectType));
            Assert.That(summary.LargestFix?.Count, Is.EqualTo(1));
            Assert.That(summary.Fixes.Select(f => (f.Cause, f.Count)), Is.EqualTo(new[]
            {
                (UnresolvedReferenceCause.NotFound, 2),
                (UnresolvedReferenceCause.AmbiguousObjectType, 1)
            }));
            Assert.That(summary.Fixes[1].Finding, Does.Not.Contain("''"),
                "The ambiguous fix speaks of the attributes as a group, not of an attribute with no name.");
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceTotalsAsync_ConnectedSystem_ReturnsItsValuesAndObjectsAsync()
    {
        UseDirectory();
        Totals(412, 127);

        var totals = await _jim.ConnectedSystems.GetUnresolvedReferenceTotalsAsync(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(totals.ReferenceCount, Is.EqualTo(412));
            Assert.That(totals.ObjectCount, Is.EqualTo(127));
        }
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceSummaryAsync_UnknownConnectedSystem_ReturnsNullAsync()
    {
        UseDirectory();

        var summary = await _jim.ConnectedSystems.GetConnectedSystemUnresolvedReferenceSummaryAsync(99);

        Assert.That(summary, Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceSummaryAsync_NothingUnresolved_SaysSoWithoutReadingAnyValuesAsync()
    {
        UseDirectory(UnresolvedReferenceHandling.Ignore);
        Totals(0, 0);

        var summary = await _jim.ConnectedSystems.GetConnectedSystemUnresolvedReferenceSummaryAsync(1);

        Assert.That(summary, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary!.TotalCount, Is.Zero);
            Assert.That(summary!.ObjectCount, Is.Zero);
            Assert.That(summary!.Fixes, Is.Empty);
            Assert.That(summary!.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Ignore));
        }

        _connectedSystems.Verify(r => r.GetConnectedSystemUnresolvedReferenceValueGroupsAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceSummaryAsync_Directory_GroupsEveryObjectsValuesWithTheObjectsBehindEachStepAsync()
    {
        UseDirectory(UnresolvedReferenceHandling.Warn);
        Totals(412, 127);
        _connectedSystems.Setup(r => r.GetConnectedSystemUnresolvedReferenceValueGroupsAsync(1)).ReturnsAsync(
        [
            Group($"ou=contractors,{Corp.ToLowerInvariant()}", $"CN=Ana Silva,OU=Contractors,{Corp}", 398, 120),
            Group($"ou=users,{Lab.ToLowerInvariant()}", $"CN=Test,OU=Users,{Lab}", 12, 5),
            Group($"ou=staff,{Corp.ToLowerInvariant()}", $"CN=Lee Grant,OU=Staff,{Corp}", 2, 2)
        ]);

        var summary = await _jim.ConnectedSystems.GetConnectedSystemUnresolvedReferenceSummaryAsync(1);

        Assert.That(summary, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary!.ConnectedSystemId, Is.EqualTo(1));
            Assert.That(summary!.UnresolvedReferenceHandling, Is.EqualTo(UnresolvedReferenceHandling.Warn));
            Assert.That(summary!.TotalCount, Is.EqualTo(412));
            Assert.That(summary!.ObjectCount, Is.EqualTo(127));
            Assert.That(summary!.Fixes.Select(f => (f.Cause, f.ContainerExternalId, f.PartitionExternalId, f.Count, f.ObjectCount)), Is.EqualTo(new[]
            {
                (UnresolvedReferenceCause.ContainerNotInScope, (string?)$"OU=Contractors,{Corp}", (string?)null, 398, (int?)120),
                (UnresolvedReferenceCause.PartitionNotSelected, null, Lab, 12, 5),
                (UnresolvedReferenceCause.NotFoundInScope, $"OU=Staff,{Corp}", null, 2, 2)
            }));
            Assert.That(summary!.LargestFix?.ContainerExternalId, Is.EqualTo($"OU=Contractors,{Corp}"));
            Assert.That(summary!.Causes.Select(c => (c.Cause, c.Count)), Is.EqualTo(new[]
            {
                (UnresolvedReferenceCause.ContainerNotInScope, 398),
                (UnresolvedReferenceCause.PartitionNotSelected, 12),
                (UnresolvedReferenceCause.NotFoundInScope, 2)
            }));
        }

        // Each step stands for one group, whose own count of objects is exact, so nothing more is read.
        _connectedSystems.Verify(r => r.GetUnresolvedReferenceObjectCountAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceSummaryAsync_GroupsNeedingTheSameChange_CountEachObjectOnceAsync()
    {
        UseDirectory();
        Totals(7, 4);
        var contractors = $"ou=contractors,{Corp.ToLowerInvariant()}";
        var temps = $"ou=temps,ou=contractors,{Corp.ToLowerInvariant()}";
        _connectedSystems.Setup(r => r.GetConnectedSystemUnresolvedReferenceValueGroupsAsync(1)).ReturnsAsync(
        [
            Group(temps, $"CN=Ben Ortiz,OU=Temps,OU=Contractors,{Corp}", 4, 3),
            Group(contractors, $"CN=Ana Silva,OU=Contractors,{Corp}", 3, 2)
        ]);
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceObjectCountAsync(1, It.Is<IReadOnlyCollection<string>>(keys => keys.Count == 2 && keys.Contains(contractors) && keys.Contains(temps))))
            .ReturnsAsync(4);

        var summary = await _jim.ConnectedSystems.GetConnectedSystemUnresolvedReferenceSummaryAsync(1);

        Assert.That(summary!.Fixes.Select(f => (f.Cause, f.Count, f.ObjectCount)), Is.EqualTo(new[]
        {
            (UnresolvedReferenceCause.ContainerNotInScope, 7, (int?)4)
        }), "An object with values under both parents is counted once, not once per parent.");
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceSummaryAsync_AnchoredSystem_SeparatesAmbiguousFromNotFoundWithTheirObjectsAsync()
    {
        UseAnchoredSystem();
        Totals(3, 2);
        _connectedSystems.Setup(r => r.GetDistinctUnresolvedReferenceValuesAsync(1, It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(ManagerAttributeId) && !ids.Contains(SecretaryAttributeId))))
            .ReturnsAsync(["42", "77"]);
        AnchorMatches(PersonAnchorId, "42");
        AnchorMatches(ContactAnchorId, "42");
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValueSplitAsync(1, It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(ManagerAttributeId)), It.Is<IReadOnlyCollection<string>>(values => values.SequenceEqual(new[] { "42" }))))
            .ReturnsAsync(new UnresolvedReferenceValueSplit { MatchingReferenceCount = 1, MatchingObjectCount = 1, OtherReferenceCount = 2, OtherObjectCount = 2 });

        var summary = await _jim.ConnectedSystems.GetConnectedSystemUnresolvedReferenceSummaryAsync(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary!.Fixes.Select(f => (f.Cause, f.Count, f.ObjectCount)), Is.EqualTo(new[]
            {
                (UnresolvedReferenceCause.NotFound, 2, (int?)2),
                (UnresolvedReferenceCause.AmbiguousObjectType, 1, (int?)1)
            }));
            Assert.That(summary!.Fixes[1].Finding, Does.Not.Contain("''"),
                "The ambiguous step speaks of the attributes as a group, not of an attribute with no name.");
            Assert.That(summary!.LargestFix?.Cause, Is.EqualTo(UnresolvedReferenceCause.AmbiguousObjectType));
        }
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceSummaryAsync_AnchoredSystemWithNothingAmbiguous_IsOneStepCoveringEveryObjectAsync()
    {
        UseAnchoredSystem();
        Totals(5, 4);
        _connectedSystems.Setup(r => r.GetDistinctUnresolvedReferenceValuesAsync(1, It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync(["77"]);

        var summary = await _jim.ConnectedSystems.GetConnectedSystemUnresolvedReferenceSummaryAsync(1);

        Assert.That(summary!.Fixes.Select(f => (f.Cause, f.Count, f.ObjectCount)), Is.EqualTo(new[]
        {
            (UnresolvedReferenceCause.NotFound, 5, (int?)4)
        }));
        _connectedSystems.Verify(r => r.GetUnresolvedReferenceValueSplitAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
    }

    #region fixtures

    private const int PersonAnchorId = 101;
    private const int ManagerAttributeId = 102;
    private const int SecretaryAttributeId = 103;
    private const int ContactAnchorId = 201;

    private void UseDirectory(UnresolvedReferenceHandling handling = UnresolvedReferenceHandling.Error)
    {
        _connector = new LdapConnector();
        _jim = new JimApplication(_repository.Object, connectorFactory: new StubConnectorFactory(_connector));

        var corp = new ConnectedSystemPartition { Id = 1, ExternalId = Corp, Name = "corp.example.com", Selected = true, Containers = [] };
        AddContainer(corp, 10, $"OU=Staff,{Corp}", selected: true);
        AddContainer(corp, 12, $"OU=Contractors,{Corp}", selected: false);
        var lab = new ConnectedSystemPartition { Id = 2, ExternalId = Lab, Name = "lab.example.com", Selected = false, Containers = [] };
        AddContainer(lab, 20, $"OU=Users,{Lab}", selected: false);

        var group = new ConnectedSystemObjectType { Id = 1, Name = "group", Selected = true };
        group.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, ConnectedSystemObjectType = group });
        group.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, ConnectedSystemObjectType = group });
        group.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "member", Type = AttributeDataType.Reference, ConnectedSystemObjectType = group });

        SetUpConnectedSystem(new ConnectedSystem
        {
            Id = 1,
            Name = "Corp AD",
            UnresolvedReferenceHandling = handling,
            ConnectorDefinition = new ConnectorDefinition { Name = "JIM LDAP Connector", SupportsPartitionContainers = true },
            Partitions = [corp, lab],
            ObjectTypes = [group]
        }, objectTypeId: 1);
    }

    private void UseAnchoredSystem()
    {
        _connector = new Mock<IConnector>().Object;
        _jim = new JimApplication(_repository.Object, connectorFactory: new StubConnectorFactory(_connector));

        var person = new ConnectedSystemObjectType { Id = 1, Name = "person", Selected = true };
        person.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = PersonAnchorId, Name = "id", Type = AttributeDataType.Text, IsExternalId = true, ConnectedSystemObjectType = person });
        person.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = ManagerAttributeId, Name = "manager", Type = AttributeDataType.Reference, ConnectedSystemObjectType = person });
        person.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = SecretaryAttributeId, Name = "secretary", Type = AttributeDataType.Reference, ReferencedObjectTypeId = 1, ConnectedSystemObjectType = person });

        var contact = new ConnectedSystemObjectType { Id = 2, Name = "contact", Selected = true };
        contact.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = ContactAnchorId, Name = "id", Type = AttributeDataType.Text, IsExternalId = true, ConnectedSystemObjectType = contact });

        SetUpConnectedSystem(new ConnectedSystem
        {
            Id = 1,
            Name = "HR Database",
            ConnectorDefinition = new ConnectorDefinition { Name = "JIM SQL Connector" },
            Partitions = [],
            ObjectTypes = [person, contact]
        }, objectTypeId: 1);
    }

    private void SetUpConnectedSystem(ConnectedSystem connectedSystem, int objectTypeId)
    {
        _connectedSystems.Setup(r => r.GetConnectedSystemAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectTypeIdAsync(1, ObjectId)).ReturnsAsync(objectTypeId);
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectsByAttributeValuesAsync(1, It.IsAny<int>(), It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, ConnectedSystemObject>());
    }

    private void Window(params (string Attribute, string Value)[] values)
    {
        var attributeIds = new Dictionary<string, int> { ["member"] = 3, ["manager"] = ManagerAttributeId, ["secretary"] = SecretaryAttributeId };
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceValuesRangeAsync(ObjectId, 0, 50, null, true))
            .ReturnsAsync(new RangeResultSet<ConnectedSystemObjectUnresolvedReferenceValue>
            {
                Results = [.. values.Select(v => new ConnectedSystemObjectUnresolvedReferenceValue { AttributeId = attributeIds[v.Attribute], AttributeName = v.Attribute, Value = v.Value })],
                TotalResults = values.Length
            });
    }

    private void Totals(int references, int objects)
    {
        _connectedSystems.Setup(r => r.GetUnresolvedReferenceTotalsAsync(1))
            .ReturnsAsync(new ConnectedSystemUnresolvedReferenceTotals { ReferenceCount = references, ObjectCount = objects });
    }

    private static ConnectedSystemUnresolvedReferenceValueGroup Group(string parentKey, string sampleValue, int count, int objectCount) =>
        new() { ParentKey = parentKey, SampleValue = sampleValue, Count = count, ObjectCount = objectCount };

    private void AnchorMatches(int anchorAttributeId, string value)
    {
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectsByAttributeValuesAsync(1, anchorAttributeId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, ConnectedSystemObject> { [value] = new() { Id = Guid.NewGuid() } });
    }

    private static void AddContainer(ConnectedSystemPartition partition, int id, string externalId, bool selected)
    {
        partition.Containers!.Add(new ConnectedSystemContainer
        {
            Id = id,
            ExternalId = externalId,
            Name = externalId,
            Selected = selected,
            Partition = partition,
            PartitionId = partition.Id
        });
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }

    #endregion
}
