// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for the fix to <c>SyncImportTaskProcessor.HydrateCsoAsync</c>'s secondary external ID
/// fallback. Before the fix, every import object with no primary external ID match (every
/// genuinely new object, and every provisioned-but-unconfirmed one) cost one
/// <c>GetConnectedSystemObjectBySecondaryExternalIdAsync</c> database round trip, even on a
/// first-ever import where no CSO could possibly exist yet (100,050 such queries measured on a
/// 100,000-object first import, all returning nothing). The fix short-circuits the lookup
/// entirely when the Connected System started empty (<c>_csIsEmpty</c>), and otherwise batches it
/// per object type per page via <c>HydrateCsoPageAsync</c>, following the #988 pattern already
/// used for primary-match hydration.
/// </summary>
[TestFixture]
public class ImportSecondaryExternalIdBatchingTests
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
    private ConnectedSystemObjectType TargetUserType { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute ObjectGuidAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute DistinguishedNameAttribute { get; set; } = null!;
    #endregion

    // "Dummy Target System" (id 2) is the only test Connected System whose object type
    // (TARGET_USER) carries a secondary external ID attribute (distinguishedName).
    private const int TargetSystemId = 2;
    private const int TargetSystemFullImportRunProfileId = 3;

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

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

        TargetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        ObjectGuidAttribute = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.ObjectGuid.ToString());
        DistinguishedNameAttribute = TargetUserType.Attributes.Single(a => a.IsSecondaryExternalId);

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        var fullImportRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Id == TargetSystemFullImportRunProfileId);
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
    }

    #region Empty Connected System short-circuit

    /// <summary>
    /// A first-ever import has no existing CSO for the secondary lookup to confirm, so
    /// <c>HydrateCsoAsync</c> must skip it entirely - neither the batched page prefetch nor the
    /// per-object fallback should ever run.
    /// </summary>
    [Test]
    public async Task PerformImportAsync_FirstImportIntoEmptyConnectedSystem_NeverCallsSecondaryLookupAsync()
    {
        ConnectedSystemObjectsData = new List<ConnectedSystemObject>();
        SetupDbContextWithCsoData();

        var countingRepo = new SecondaryLookupCountingSyncRepository();
        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First(), repository: countingRepo);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        for (var i = 0; i < 5; i++)
            mockFileConnector.TestImportObjects.Add(CreateImportObject(Guid.NewGuid(), $"CN=Person {i},DC=test"));

        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        Assert.That(countingRepo.SingleSecondaryLookupCalls, Is.Zero,
            "A first-ever import has no existing CSO to confirm via secondary external ID; HydrateCsoAsync must short-circuit before issuing the per-object lookup.");
        Assert.That(countingRepo.BatchSecondaryLookupCalls, Is.Zero,
            "The page batch prefetch must not run either, for the same reason: HydrateCsoPageAsync already returns early when the Connected System is empty.");
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(5), "every imported object should have been created as new");
    }

    #endregion

    #region Per-page batching: confirming Pending Provisioning CSOs

    /// <summary>
    /// Several Pending Provisioning CSOs (provisioned by JIM but not yet confirmed - no primary
    /// ObjectGuid value yet, only the distinguishedName anchor used at creation time) must each be
    /// matched and transitioned to Normal via ONE batch call for the whole page, not one
    /// per-object query per candidate.
    /// </summary>
    [Test]
    public async Task PerformImportAsync_SeveralPendingProvisioningCsosConfirmedBySecondaryId_UsesOneBatchCallAndZeroSingleQueriesAsync()
    {
        var distinguishedNames = new[] { "CN=Alice,DC=test", "CN=Bob,DC=test", "CN=Carol,DC=test" };
        ConnectedSystemObjectsData = distinguishedNames.Select(CreatePendingProvisioningCso).ToList();
        SetupDbContextWithCsoData();

        var countingRepo = new SecondaryLookupCountingSyncRepository();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First(), repository: countingRepo);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        foreach (var dn in distinguishedNames)
            mockFileConnector.TestImportObjects.Add(CreateImportObject(Guid.NewGuid(), dn));

        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        Assert.That(countingRepo.SingleSecondaryLookupCalls, Is.Zero,
            "All three candidates are resolvable from the page's single batch call; none should fall back to the per-object query.");
        Assert.That(countingRepo.BatchSecondaryLookupCalls, Is.EqualTo(1),
            "One object type, one page: the batch lookup must run exactly once for the whole page, not once per object.");

        // No new CSOs should have been created: every import object confirmed an existing
        // Pending Provisioning CSO and transitioned it to Normal.
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(3));
        foreach (var cso in ConnectedSystemObjectsData)
        {
            Assert.That(SyncRepo.ConnectedSystemObjects[cso.Id].Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
                $"CSO {cso.Id} should have been confirmed via the batched secondary lookup and transitioned to Normal.");
        }
    }

    /// <summary>
    /// Two CSOs sharing the same secondary external ID value is ambiguous. The batch query must
    /// leave that value uncovered rather than guessing, so <c>HydrateCsoAsync</c> falls back to the
    /// single-object query for it (which, against a real database, is exactly what preserves
    /// <c>GetConnectedSystemObjectBySecondaryExternalIdAsync</c>'s <c>SingleOrDefaultAsync</c>
    /// throw-on-duplicate behaviour; the in-memory double does not throw, but the call still proves
    /// the fallback was reached).
    /// </summary>
    [Test]
    public async Task PerformImportAsync_DuplicateSecondaryIdAcrossTwoCsos_FallsBackToSingleQueryAsync()
    {
        const string duplicateDn = "CN=Duplicate,DC=test";
        var first = CreatePendingProvisioningCso(duplicateDn);
        var second = CreatePendingProvisioningCso(duplicateDn);
        ConnectedSystemObjectsData = new List<ConnectedSystemObject> { first, second };
        SetupDbContextWithCsoData();

        var countingRepo = new SecondaryLookupCountingSyncRepository();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First(), repository: countingRepo);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateImportObject(Guid.NewGuid(), duplicateDn));

        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        Assert.That(countingRepo.BatchSecondaryLookupCalls, Is.EqualTo(1),
            "The page batch must still run once, even though it cannot resolve this ambiguous value.");
        Assert.That(countingRepo.SingleSecondaryLookupCalls, Is.EqualTo(1),
            "A value matched by more than one CSO must fall back to the single-object query rather than guessing.");
    }

    /// <summary>
    /// A secondary external ID value matched by exactly one CSO that is already Normal (not
    /// Pending Provisioning) is a confirmed "no match": the primary ID lookup would have found it
    /// already if it still existed under that identity. This must be resolved from the batch alone
    /// (no per-object fallback), and the import object must be treated as new, exactly as it is
    /// today for any object with no matching CSO.
    /// </summary>
    [Test]
    public async Task PerformImportAsync_SecondaryIdMatchesOnlyNormalStatusCso_CreatesNewCsoWithoutSingleQueryAsync()
    {
        const string dn = "CN=AlreadyNormal,DC=test";
        // Deliberately built the same way as the Pending Provisioning fixtures (no primary
        // ObjectGuid value), so the miss goes straight to the secondary path under test; only the
        // status differs.
        var existing = CreatePendingProvisioningCso(dn);
        existing.Status = ConnectedSystemObjectStatus.Normal;
        ConnectedSystemObjectsData = new List<ConnectedSystemObject> { existing };
        SetupDbContextWithCsoData();

        var countingRepo = new SecondaryLookupCountingSyncRepository();
        SyncRepo = TestUtilities.CreateSyncRepository(csos: ConnectedSystemObjectsData, activity: ActivitiesData.First(), repository: countingRepo);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(CreateImportObject(Guid.NewGuid(), dn));

        var processor = await CreateProcessorAsync(mockFileConnector);
        await processor.PerformImportAsync();

        Assert.That(countingRepo.BatchSecondaryLookupCalls, Is.EqualTo(1));
        Assert.That(countingRepo.SingleSecondaryLookupCalls, Is.Zero,
            "A single, non-Pending-Provisioning match is a confirmed 'no match' from the batch alone; it must not fall back to the single query.");

        // A brand new CSO must have been created for the imported object, exactly as it would be
        // today for any import object with no matching existing CSO.
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(2));
        Assert.That(SyncRepo.ConnectedSystemObjects[existing.Id].Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
            "the pre-existing CSO must be untouched");
    }

    #endregion

    #region Private helpers

    private async Task<JIM.Worker.Processors.SyncImportTaskProcessor> CreateProcessorAsync(MockFileConnector connector)
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(TargetSystemId);
        Assert.That(connectedSystem, Is.Not.Null, "Expected to retrieve the target Connected System.");

        var runProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Id == TargetSystemFullImportRunProfileId);
        var activity = ActivitiesData.First();

        return new JIM.Worker.Processors.SyncImportTaskProcessor(
            Jim, SyncRepo, new JIM.Application.Servers.SyncServer(Jim), new JIM.Application.Servers.SyncEngine(),
            connector, connectedSystem!, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy),
            new CancellationTokenSource());
    }

    private void SetupDbContextWithCsoData()
    {
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
    }

    /// <summary>
    /// Builds a TARGET_USER import object carrying only the primary (ObjectGuid) and secondary
    /// (distinguishedName) external IDs - enough for the matching logic under test, without the
    /// noise of every other TARGET_USER attribute.
    /// </summary>
    private static ConnectedSystemImportObject CreateImportObject(Guid objectGuid, string distinguishedName)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "TARGET_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    Name = MockTargetSystemAttributeNames.ObjectGuid.ToString(),
                    GuidValues = new List<Guid> { objectGuid }
                },
                new()
                {
                    Name = "distinguishedName",
                    StringValues = new List<string> { distinguishedName }
                }
            }
        };
    }

    /// <summary>
    /// Creates a Pending Provisioning CSO with no primary (ObjectGuid) value yet - the state a
    /// provisioned-but-unconfirmed object is in before a confirming import reveals the target
    /// system's real, system-assigned primary ID - carrying only the distinguishedName anchor
    /// recorded at provisioning time.
    /// </summary>
    private ConnectedSystemObject CreatePendingProvisioningCso(string distinguishedName)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            ConnectedSystem = ConnectedSystemsData.Single(cs => cs.Id == TargetSystemId),
            Type = TargetUserType,
            TypeId = TargetUserType.Id,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            ExternalIdAttributeId = ObjectGuidAttribute.Id,
            SecondaryExternalIdAttributeId = DistinguishedNameAttribute.Id,
            Created = DateTime.UtcNow.AddMinutes(-5)
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = DistinguishedNameAttribute.Id,
            Attribute = DistinguishedNameAttribute,
            ConnectedSystemObject = cso,
            StringValue = distinguishedName
        });
        return cso;
    }

    /// <summary>
    /// Spy repository that counts calls to the per-object and page-batched secondary external ID
    /// lookups, so tests can assert the import pipeline uses the batch instead of the single-object
    /// query it used exclusively before this fix.
    /// </summary>
    private sealed class SecondaryLookupCountingSyncRepository : SyncRepository
    {
        public int SingleSecondaryLookupCalls;
        public int BatchSecondaryLookupCalls;

        public override Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(
            int connectedSystemId, int objectTypeId, string secondaryExternalIdValue)
        {
            Interlocked.Increment(ref SingleSecondaryLookupCalls);
            return base.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryExternalIdValue);
        }

        public override Task<IReadOnlyList<(string Value, Guid ConnectedSystemObjectId, ConnectedSystemObjectStatus Status)>> GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            int connectedSystemId, int objectTypeId, int secondaryExternalIdAttributeId, IReadOnlyCollection<string> secondaryExternalIdValues)
        {
            Interlocked.Increment(ref BatchSecondaryLookupCalls);
            return base.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(connectedSystemId, objectTypeId, secondaryExternalIdAttributeId, secondaryExternalIdValues);
        }
    }

    #endregion
}
