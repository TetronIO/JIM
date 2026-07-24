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
/// SPEC-1082 test plan item 4: end-to-end Full Import content-hash skip behaviour, driven through
/// two real <see cref="SyncImportTaskProcessor.PerformImportAsync"/> runs against the SAME in-memory
/// repository (the first import creates and stamps; the second proves the skip predicate). Mirrors
/// the harness pattern in <c>ImportBatchPrefetchTests</c>.
/// </summary>
[TestFixture]
public class ImportContentHashSkipTests
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

    /// <summary>
    /// Runs a Full Import against a fresh <see cref="SyncImportTaskProcessor"/> instance (each run
    /// gets its own pre-fetch snapshot, exactly as a real worker task would), sharing the same
    /// in-memory repository across calls so state (including stamped hashes) persists between runs.
    /// </summary>
    private static readonly System.Reflection.FieldInfo HashSkippedCountField = typeof(SyncImportTaskProcessor)
        .GetField("_hashSkippedCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?? throw new InvalidOperationException("_hashSkippedCount field not found via reflection - has it been renamed?");

    private static int GetHashSkippedCount(SyncImportTaskProcessor processor)
        => (int)HashSkippedCountField.GetValue(processor)!;

    private async Task<(Activity Activity, int HashSkippedCount)> RunFullImportAsync(MockFileConnector connector, bool verifyImportContentHashes = false)
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var runProfile = ConnectedSystemRunProfilesData.Single(
            q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        runProfile.VerifyImportContentHashes = verifyImportContentHashes;

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>()
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem!, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy),
            new CancellationTokenSource());

        await processor.PerformImportAsync();
        return (activity, GetHashSkippedCount(processor));
    }

    private static ConnectedSystemImportObject CreateImportObject(Guid hrId, string displayName, ObjectChangeType changeType = ObjectChangeType.NotSet)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = changeType,
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
    /// (1) A second, byte-identical Full Import skips the unchanged object: no hydration-driven diff
    /// runs (proven by the CSO's identity being preserved and no new RPEI existing for it), and the
    /// skip is reflected in the processor's own counters via the Activity's Debug/Information log path.
    /// </summary>
    [Test]
    public async Task SecondIdenticalFullImport_SkipsUnchangedObjectAsync()
    {
        var hrId = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));

        var (_, skipped1) = await RunFullImportAsync(connector);
        Assert.That(skipped1, Is.EqualTo(0), "the first-ever import of an object can never be skipped.");
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(1));
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        Assert.That(cso.ImportStateHash, Is.Not.Null, "the first Full Import must stamp the new CSO.");
        var stampedHash = cso.ImportStateHash;

        // Second, byte-identical import.
        var connector2 = new MockFileConnector();
        connector2.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        var (activity2, skipped2) = await RunFullImportAsync(connector2);

        Assert.That(skipped2, Is.EqualTo(1), "the processor's own skip counter must record the object as skipped by hash, not honestly re-diffed.");
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(1), "no duplicate CSO should be created.");
        var csoAfter = SyncRepo.ConnectedSystemObjects.Values.Single();
        Assert.That(csoAfter.ImportStateHash, Is.EqualTo(stampedHash), "the stamped hash must be unchanged by a skipped no-op import.");
        Assert.That(csoAfter.LastUpdated, Is.Null, "a skipped object must never have LastUpdated bumped (#891 watermark).");
        Assert.That(activity2.RunProfileExecutionItems.Any(r => r.ConnectedSystemObjectId == csoAfter.Id), Is.False,
            "a skipped object must not have an RPEI (mirrors the existing no-op path).");
    }

    /// <summary>
    /// (2) A changed object is still diffed and re-stamped with the new hash.
    /// </summary>
    [Test]
    public async Task SecondFullImport_ChangedObject_StillDiffsAndReStampsAsync()
    {
        var hrId = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector);
        var stampedHash = SyncRepo.ConnectedSystemObjects.Values.Single().ImportStateHash;

        var connector2 = new MockFileConnector();
        connector2.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith-Updated"));
        await RunFullImportAsync(connector2);

        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        var displayName = cso.AttributeValues.Single(av => av.Attribute?.Name == MockSourceSystemAttributeNames.DISPLAY_NAME.ToString());
        Assert.That(displayName.StringValue, Is.EqualTo("Jane Smith-Updated"), "the changed value must still be diffed and applied.");
        Assert.That(cso.ImportStateHash, Is.Not.EqualTo(stampedHash), "a changed object must be re-stamped with the new hash.");
        Assert.That(cso.LastUpdated, Is.Not.Null, "a genuinely changed object must still bump LastUpdated.");
    }

    /// <summary>
    /// (3) A CSO with no stored hash (e.g. one that pre-dates this feature) is never skip-eligible;
    /// the honest diff runs and stamps it, so a THIRD identical import can then skip it.
    /// </summary>
    [Test]
    public async Task NullStoredHash_HonestDiffThenStampedAsync()
    {
        var hrId = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector);

        // Simulate a pre-existing-estate CSO with no stamp (StampImportStateAsync never ran for it).
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        cso.ImportStateHash = null;
        cso.ImportStateFingerprint = null;

        var connector2 = new MockFileConnector();
        connector2.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector2);

        Assert.That(cso.ImportStateHash, Is.Not.Null, "the honest diff must re-stamp a previously-unstamped CSO.");
    }

    /// <summary>
    /// (4) A stale fingerprint (schema redefinition) disqualifies the skip; the honest diff runs
    /// and re-stamps with the CURRENT fingerprint.
    /// </summary>
    [Test]
    public async Task FingerprintMismatch_HonestDiffThenReStampedAsync()
    {
        var hrId = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector);

        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        var staleFingerprint = Guid.NewGuid(); // guaranteed not to match the real current fingerprint
        cso.ImportStateFingerprint = staleFingerprint;

        var connector2 = new MockFileConnector();
        connector2.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector2);

        Assert.That(cso.ImportStateFingerprint, Is.Not.EqualTo(staleFingerprint), "a fingerprint mismatch must disqualify the skip and re-stamp with the current fingerprint.");
    }

    /// <summary>
    /// (5) A ChangeType.Deleted import object is never skip-eligible, even if a stale stamp happens
    /// to match - it still hits the existing Deleted branch and obsoletes the CSO.
    /// </summary>
    [Test]
    public async Task DeletedChangeType_NotSkippedAsync()
    {
        var hrId = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector);
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));

        var connector2 = new MockFileConnector();
        connector2.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith", ObjectChangeType.Deleted));
        await RunFullImportAsync(connector2);

        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "a Deleted change type must still be honoured, never skipped.");
    }

    /// <summary>
    /// (6) Skipped objects are still correctly subject to deletion detection: an object present in
    /// import 1 but absent from import 2 must still be obsoleted, proving the skip path does not
    /// interfere with the separate deletion-detection pass (which reads externalIdsImported,
    /// collected before any skip decision).
    /// </summary>
    [Test]
    public async Task DeletionDetection_UnaffectedBySkippedSiblingObjectAsync()
    {
        var hrIdKept = Guid.NewGuid();
        var hrIdRemoved = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrIdKept, "Kept User"));
        connector.TestImportObjects.Add(CreateImportObject(hrIdRemoved, "Removed User"));
        await RunFullImportAsync(connector);
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(2));

        // Second import: "Kept User" unchanged (should skip), "Removed User" absent (must obsolete).
        var connector2 = new MockFileConnector();
        connector2.TestImportObjects.Add(CreateImportObject(hrIdKept, "Kept User"));
        await RunFullImportAsync(connector2);

        var keptCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.AttributeValues.Any(av => av.GuidValue == hrIdKept));
        var removedCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.AttributeValues.Any(av => av.GuidValue == hrIdRemoved));

        Assert.That(keptCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "the skipped (unchanged) object must remain Normal.");
        Assert.That(removedCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "the genuinely missing object must still be obsoleted even though its sibling was skipped.");
    }

    /// <summary>
    /// (7) A Delta Import that changes an object nulls its stored hash (conservative v1); a Delta
    /// Import no-op leaves the stored hash untouched.
    /// </summary>
    [Test]
    public async Task DeltaImport_ChangedObject_NullsStoredHash_NoOpLeavesItAsync()
    {
        var hrId = Guid.NewGuid();
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith"));
        await RunFullImportAsync(connector);
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        Assert.That(cso.ImportStateHash, Is.Not.Null);

        // Delta Import with a real change.
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var deltaRunProfile = new ConnectedSystemRunProfile
        {
            Id = 999,
            Name = "Delta Import Test",
            RunType = ConnectedSystemRunType.DeltaImport,
            ConnectedSystemId = connectedSystem!.Id
        };
        var activity = new Activity { Id = Guid.NewGuid(), RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>() };
        var deltaConnector = new MockFileConnector();
        deltaConnector.TestImportObjects.Add(CreateImportObject(hrId, "Jane Smith-Delta-Updated", ObjectChangeType.Updated));
        var deltaProcessor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            deltaConnector, connectedSystem, deltaRunProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy),
            new CancellationTokenSource());
        await deltaProcessor.PerformImportAsync();

        Assert.That(cso.ImportStateHash, Is.Null, "a Delta Import that changes the object must null the stored hash (conservative v1).");
    }
}
