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
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// SPEC-1082 test plan item 5: Run Profile Verification Mode. When
/// <see cref="ConnectedSystemRunProfile.VerifyImportContentHashes"/> is true, a Full Import performs
/// no skips (proven by <c>_hashSkippedCount</c> staying zero even when the hash would otherwise
/// qualify) and instead compares the stored hash against the honest diff's own findings.
/// </summary>
[TestFixture]
public class ImportContentHashVerificationModeTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; } = null!;
    private List<ServiceSetting> ServiceSettingsData { get; set; } = null!;
    private Mock<DbSet<ServiceSetting>> MockDbSetServiceSettings { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = new();
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    #endregion

    [TearDown]
    public void TearDown() => Jim?.Dispose();

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        ServiceSettingsData = TestUtilities.GetServiceSettingsData();
        MockDbSetServiceSettings = ServiceSettingsData.BuildMockDbSet();

        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(MockDbSetServiceSettings.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        ConnectedSystemObjectsData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = ConnectedSystemObjectsData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>()))
            .Callback((IEnumerable<ConnectedSystemObject> entities) =>
            {
                var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
                foreach (var entity in connectedSystemObjects)
                    entity.Id = Guid.NewGuid();
                ConnectedSystemObjectsData.AddRange(connectedSystemObjects);
            });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);

        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    private static readonly System.Reflection.FieldInfo HashSkippedCountField = typeof(SyncImportTaskProcessor)
        .GetField("_hashSkippedCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    private static int GetHashSkippedCount(SyncImportTaskProcessor p) => (int)HashSkippedCountField.GetValue(p)!;

    private async Task<Activity> RunFullImportAsync(MockFileConnector connector, bool verify)
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var runProfile = ConnectedSystemRunProfilesData.Single(
            q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        runProfile.VerifyImportContentHashes = verify;

        var activity = new Activity { Id = Guid.NewGuid(), RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>() };
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem!, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy),
            new CancellationTokenSource());

        await processor.PerformImportAsync();
        Assert.That(GetHashSkippedCount(processor), Is.EqualTo(0), "Verification Mode must never skip.");
        return activity;
    }

    private static ConnectedSystemImportObject CreateImportObject(Guid hrId, string displayName)
    {
        return new ConnectedSystemImportObject
        {
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { hrId } },
                new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = new List<string> { displayName } },
                new() { Name = MockSourceSystemAttributeNames.EMPLOYEE_ID.ToString(), IntValues = new List<int> { 1 } }
            }
        };
    }

    /// <summary>
    /// (1) Verification Mode with a genuinely unchanged object: no skip (proven above), no error,
    /// benign counter path only (no assertion on the private counter here; behaviourally proven by
    /// the absence of an ImportHashVerificationFailed RPEI).
    /// </summary>
    [Test]
    public async Task VerificationMode_UnchangedObject_NoErrorAsync()
    {
        var hrId = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector, verify: false);

        var connector2 = new MockFileConnector();
        connector2.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        var activity2 = await RunFullImportAsync(connector2, verify: true);

        Assert.That(activity2.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ImportHashVerificationFailed), Is.False);
    }

    /// <summary>
    /// (2) Dangerous disagreement: seed a stored hash that matches the incoming hash but then
    /// corrupt the stored attribute value directly (bypassing the normal write path, simulating a
    /// hypothetical calculator/diff divergence) so the honest diff still finds a change.
    /// Verification Mode must raise ImportHashVerificationFailed.
    /// </summary>
    [Test]
    public async Task VerificationMode_StoredHashMatchesButDiffFindsChanges_RaisesDangerousDisagreementAsync()
    {
        var hrId = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector, verify: false);

        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        var stampedHash = cso.ImportStateHash;
        Assert.That(stampedHash, Is.Not.Null);

        // Simulate the "dangerous disagreement" scenario: the stored hash still describes the OLD
        // value (as if a mutation path forgot to null it, D9's failure mode), but the stored
        // attribute value was changed without re-stamping.
        var displayNameValue = cso.AttributeValues.Single(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        displayNameValue.StringValue = "Corrupted Without Re-Stamp";
        cso.ImportStateHash = stampedHash; // force the stale hash to remain, simulating the bug this mode exists to catch

        var connector2 = new MockFileConnector();
        connector2.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith")); // matches the ORIGINAL value, so incoming hash == stampedHash
        var activity2 = await RunFullImportAsync(connector2, verify: true);

        var errorRpei = activity2.RunProfileExecutionItems.SingleOrDefault(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ImportHashVerificationFailed);
        Assert.That(errorRpei, Is.Not.Null, "a stored-hash-matches-but-diff-found-changes disagreement must raise ImportHashVerificationFailed.");

        // The object's changes must still be applied normally (verification is diagnostic-only):
        // the diff replaces the corrupted value with a new value matching the import, rather than
        // mutating the old attribute-value instance in place.
        var currentDisplayName = cso.AttributeValues.Single(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        Assert.That(currentDisplayName.StringValue, Is.EqualTo("Jane Smith"));
    }

    /// <summary>
    /// (3) Toggling Verification Mode on a non-Full-Import Run Profile is a portal/REST validation
    /// concern (400 on create/update), not an import-processor concern; the processor itself simply
    /// never applies Verification Mode semantics for a Delta Import (only the RunType == FullImport
    /// gate matters at runtime). See JIM.Web.Api.Tests for the DTO-level validation tests.
    /// </summary>
    [Test]
    public void VerifyImportContentHashes_DefaultsFalse()
    {
        var runProfile = new ConnectedSystemRunProfile { Name = "Test", RunType = ConnectedSystemRunType.FullImport, ConnectedSystemId = 1 };
        Assert.That(runProfile.VerifyImportContentHashes, Is.False);
    }
}
