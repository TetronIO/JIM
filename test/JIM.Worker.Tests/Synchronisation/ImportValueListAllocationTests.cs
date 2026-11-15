// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// #2521: an import attribute allocates a typed value list only when it is used, so a text attribute carries one list,
/// not seven. That saves nothing unless the import reads every other list without allocating it too, so these run real
/// Full Imports, creating and then updating an object, and check what each attribute the Connector returned holds after.
/// </summary>
[TestFixture]
public class ImportValueListAllocationTests
{
    private static readonly string HrId = MockSourceSystemAttributeNames.HR_ID.ToString();
    private static readonly string EmployeeNumber = MockSourceSystemAttributeNames.EMPLOYEE_NUMBER.ToString();
    private static readonly string DisplayName = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString();
    private static readonly string Qualifications = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString();

    private MetaverseObject _initiatedBy = null!;
    private List<ConnectedSystemRunProfile> _runProfiles = null!;
    private JimApplication _jim = null!;
    private SyncRepository _syncRepo = null!;

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _initiatedBy = TestUtilities.GetInitiatedBy();

        var connectedSystems = TestUtilities.GetConnectedSystemData();
        _runProfiles = TestUtilities.GetConnectedSystemRunProfileData();
        var objectTypes = TestUtilities.GetConnectedSystemObjectTypeData();
        var partitions = TestUtilities.GetConnectedSystemPartitionData();
        var fullImportRunProfile = _runProfiles[0];
        var activities = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        var serviceSettings = TestUtilities.GetServiceSettingsData();

        var mockDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(mockDbContext);
        mockDbContext.Setup(m => m.Activities).Returns(activities.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystems).Returns(connectedSystems.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(objectTypes.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(_runProfiles.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(partitions.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ServiceSettingItems).Returns(serviceSettings.BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.PendingExports).Returns(new List<PendingExport>().BuildMockDbSet().Object);
        mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(new List<ConnectedSystemObject>().BuildMockDbSet().Object);

        _syncRepo = TestUtilities.CreateSyncRepository(activity: activities.First());
        _jim = new JimApplication(new PostgresDataRepository(mockDbContext.Object), syncRepository: _syncRepo);
    }

    [Test]
    public async Task FullImport_NewObject_EachAttributeHoldsOnlyItsOwnValueListAsync()
    {
        var person = Person(Guid.NewGuid(), "Ada Ashcroft");

        await RunFullImportAsync(person);

        Assert.That(_syncRepo.ConnectedSystemObjects, Has.Count.EqualTo(1), "the object was imported");
        AssertEachAttributeHoldsOnlyItsOwnList(person);
    }

    [Test]
    public async Task FullImport_ChangedObject_EachAttributeHoldsOnlyItsOwnValueListAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(Person(hrId, "Ada Ashcroft"));
        var changed = Person(hrId, "Ada Ashcroft-Byrne");

        await RunFullImportAsync(changed);

        var stored = _syncRepo.ConnectedSystemObjects.Values.Single();
        Assert.That(stored.AttributeValues.Where(av => av.Attribute?.Name == DisplayName).Select(av => av.StringValue),
            Is.EqualTo(new[] { "Ada Ashcroft-Byrne" }), "the change was imported");
        AssertEachAttributeHoldsOnlyItsOwnList(changed);
    }

    private static void AssertEachAttributeHoldsOnlyItsOwnList(ConnectedSystemImportObject importObject)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ImportAttributeValueLists.Allocated(Attribute(importObject, HrId)), Is.EqualTo(new[] { "GuidValues" }));
            Assert.That(ImportAttributeValueLists.Allocated(Attribute(importObject, EmployeeNumber)), Is.EqualTo(new[] { "IntValues" }));
            Assert.That(ImportAttributeValueLists.Allocated(Attribute(importObject, DisplayName)), Is.EqualTo(new[] { "StringValues" }));
            Assert.That(ImportAttributeValueLists.Allocated(Attribute(importObject, Qualifications)), Is.EqualTo(new[] { "StringValues" }));
        }
    }

    private static ConnectedSystemImportObjectAttribute Attribute(ConnectedSystemImportObject importObject, string name) =>
        importObject.Attributes.Single(a => a.Name == name);

    private async Task RunFullImportAsync(ConnectedSystemImportObject importObject)
    {
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(importObject);

        var connectedSystem = await _jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var runProfile = _runProfiles.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>()
        };

        var processor = new SyncImportTaskProcessor(
            _jim, _syncRepo, new SyncServer(_jim), new SyncEngine(),
            connector, connectedSystem!, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, _initiatedBy),
            new CancellationTokenSource());

        await processor.PerformImportAsync();
    }

    // Each attribute is built by adding to its one list, as a Connector does.
    private static ConnectedSystemImportObject Person(Guid hrId, string displayName)
    {
        var hrIdAttribute = new ConnectedSystemImportObjectAttribute { Name = HrId };
        hrIdAttribute.GuidValues.Add(hrId);
        var employeeNumber = new ConnectedSystemImportObjectAttribute { Name = EmployeeNumber };
        employeeNumber.IntValues.Add(1001);
        var name = new ConnectedSystemImportObjectAttribute { Name = DisplayName };
        name.StringValues.Add(displayName);
        var qualifications = new ConnectedSystemImportObjectAttribute { Name = Qualifications };
        qualifications.StringValues.AddRange(["CERT-A", "CERT-B", "CERT-A"]);

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = [hrIdAttribute, employeeNumber, name, qualifications]
        };
    }
}
