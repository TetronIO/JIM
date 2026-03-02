using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests that the import processor correctly builds sync outcome trees on RPEIs.
/// Uses the full workflow pipeline (mocked DB) to verify outcome wiring.
///
/// Note: Delete and Update outcome tests require complex CSO setup with full back-references
/// and are covered by the existing ImportDeleteObjectTests and ImportUpdateObject*Tests.
/// This test focuses on the Add path to verify outcome wiring without duplicating that setup.
/// </summary>
[TestFixture]
public class ImportExportOutcomeTests
{
    private MetaverseObject _initiatedBy = null!;
    private List<ConnectedSystem> _connectedSystemsData = null!;
    private List<ConnectedSystemRunProfile> _runProfilesData = null!;
    private List<ConnectedSystemObjectType> _objectTypesData = null!;
    private List<ConnectedSystemPartition> _partitionsData = null!;
    private List<Activity> _activitiesData = null!;
    private List<ServiceSetting> _serviceSettingsData = null!;
    private List<PendingExport> _pendingExportsData = null!;
    private Mock<JimDbContext> _mockDbContext = null!;
    private JimApplication _jim = null!;

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _initiatedBy = TestUtilities.GetInitiatedBy();

        _connectedSystemsData = TestUtilities.GetConnectedSystemData();
        _runProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        _objectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        _partitionsData = TestUtilities.GetConnectedSystemPartitionData();
        _serviceSettingsData = TestUtilities.GetServiceSettingsData();
        _pendingExportsData = new List<PendingExport>();

        var fullImportRunProfile = _runProfilesData[0];
        _activitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);

        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.Activities).Returns(_activitiesData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ConnectedSystems).Returns(_connectedSystemsData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(_objectTypesData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(_runProfilesData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(_partitionsData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.ServiceSettingItems).Returns(_serviceSettingsData.BuildMockDbSet().Object);
        _mockDbContext.Setup(m => m.PendingExports).Returns(_pendingExportsData.BuildMockDbSet().Object);
    }

    [Test]
    public async Task FullImport_NewObjects_RpeisHaveCsoAddedOutcomeAsync()
    {
        // Arrange - empty CSO list (first-ever import, all objects are new)
        var csoData = new List<ConnectedSystemObject>();
        var mockDbSet = csoData.BuildMockDbSet();
        mockDbSet.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                foreach (var entity in entities)
                    entity.Id = Guid.NewGuid();
                csoData.AddRange(entities);
            });
        _mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSet.Object);
        _mockDbContext.Setup(m => m.AddRange(It.IsAny<IEnumerable<object>>()));
        _jim = new JimApplication(new PostgresDataRepository(_mockDbContext.Object));

        var mockConnector = new MockFileConnector();
        mockConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(), IntValues = new List<int> { 1 } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "Test User" } }
            }
        });

        var connectedSystem = await _jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var activity = _activitiesData.First();
        var runProfile = _runProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var workerTask = TestUtilities.CreateTestWorkerTask(activity, _initiatedBy);

        // Act
        var processor = new SyncImportTaskProcessor(_jim, mockConnector, connectedSystem!, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformFullImportAsync();

        // Assert - RPEIs should have CsoAdded outcomes (default tracking level is Detailed)
        var rpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.Added)
            .ToList();
        Assert.That(rpeis, Has.Count.EqualTo(1), "Expected one Added RPEI");

        var rpei = rpeis[0];
        Assert.That(rpei.SyncOutcomes, Has.Count.EqualTo(1));
        Assert.That(rpei.SyncOutcomes[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded));
        Assert.That(rpei.SyncOutcomes[0].DetailCount, Is.EqualTo(3), "Should record imported attribute count");
        Assert.That(rpei.OutcomeSummary, Is.EqualTo("CsoAdded:1"));
    }

    [Test]
    public async Task FullImport_MultipleNewObjects_EachHasIndependentOutcomesAsync()
    {
        // Arrange
        var csoData = new List<ConnectedSystemObject>();
        var mockDbSet = csoData.BuildMockDbSet();
        mockDbSet.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                foreach (var entity in entities)
                    entity.Id = Guid.NewGuid();
                csoData.AddRange(entities);
            });
        _mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSet.Object);
        _mockDbContext.Setup(m => m.AddRange(It.IsAny<IEnumerable<object>>()));
        _jim = new JimApplication(new PostgresDataRepository(_mockDbContext.Object));

        var mockConnector = new MockFileConnector();
        // Object 1: 3 attributes
        mockConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(), IntValues = new List<int> { 1 } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "User One" } }
            }
        });
        // Object 2: 5 attributes
        mockConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(), IntValues = new List<int> { 2 } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "User Two" } },
                new() { Name = MockSourceSystemAttributeNames.EMAIL_ADDRESS.ToString(), StringValues = new List<string> { "two@test.com" } },
                new() { Name = MockSourceSystemAttributeNames.ROLE.ToString(), StringValues = new List<string> { "Developer" } }
            }
        });

        var connectedSystem = await _jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var activity = _activitiesData.First();
        var runProfile = _runProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var workerTask = TestUtilities.CreateTestWorkerTask(activity, _initiatedBy);

        // Act
        var processor = new SyncImportTaskProcessor(_jim, mockConnector, connectedSystem!, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformFullImportAsync();

        // Assert - two Added RPEIs, each with independent CsoAdded outcomes
        var addedRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.Added)
            .ToList();
        Assert.That(addedRpeis, Has.Count.EqualTo(2));

        Assert.That(addedRpeis[0].SyncOutcomes, Has.Count.EqualTo(1));
        Assert.That(addedRpeis[0].SyncOutcomes[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded));
        Assert.That(addedRpeis[0].SyncOutcomes[0].DetailCount, Is.EqualTo(3));
        Assert.That(addedRpeis[0].OutcomeSummary, Is.EqualTo("CsoAdded:1"));

        Assert.That(addedRpeis[1].SyncOutcomes, Has.Count.EqualTo(1));
        Assert.That(addedRpeis[1].SyncOutcomes[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded));
        Assert.That(addedRpeis[1].SyncOutcomes[0].DetailCount, Is.EqualTo(5));
        Assert.That(addedRpeis[1].OutcomeSummary, Is.EqualTo("CsoAdded:1"));

        // Each outcome should have a unique ID (pre-generated by FlattenSyncOutcomes)
        var allOutcomeIds = addedRpeis.SelectMany(r => r.SyncOutcomes).Select(o => o.Id).ToList();
        Assert.That(allOutcomeIds.Distinct().Count(), Is.EqualTo(2), "Each outcome should have a unique ID");
        Assert.That(allOutcomeIds.All(id => id != Guid.Empty), Is.True, "All outcome IDs should be pre-generated");
    }

    [Test]
    public async Task FullImport_ErrorObjects_NoOutcomesOnErrorRpeisAsync()
    {
        // Arrange - import object with an unexpected attribute that causes an error
        var csoData = new List<ConnectedSystemObject>();
        var mockDbSet = csoData.BuildMockDbSet();
        mockDbSet.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                foreach (var entity in entities)
                    entity.Id = Guid.NewGuid();
                csoData.AddRange(entities);
            });
        _mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSet.Object);
        _mockDbContext.Setup(m => m.AddRange(It.IsAny<IEnumerable<object>>()));
        _jim = new JimApplication(new PostgresDataRepository(_mockDbContext.Object));

        var mockConnector = new MockFileConnector();
        // Good object
        mockConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { "Good User" } }
            }
        });
        // Bad object - has attribute not in schema
        mockConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = "NONEXISTENT_ATTRIBUTE", StringValues = new List<string> { "bad" } }
            }
        });

        var connectedSystem = await _jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var activity = _activitiesData.First();
        var runProfile = _runProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var workerTask = TestUtilities.CreateTestWorkerTask(activity, _initiatedBy);

        // Act
        var processor = new SyncImportTaskProcessor(_jim, mockConnector, connectedSystem!, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformFullImportAsync();

        // Assert - good object has outcome, error object does not
        var addedRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.Added && r.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();
        Assert.That(addedRpeis, Has.Count.EqualTo(1));
        Assert.That(addedRpeis[0].SyncOutcomes, Has.Count.EqualTo(1));

        var errorRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();
        Assert.That(errorRpeis, Has.Count.EqualTo(1));
        // Error RPEIs don't get CsoAdded outcome because CSO creation failed
        Assert.That(errorRpeis[0].SyncOutcomes, Is.Empty);
    }
}
