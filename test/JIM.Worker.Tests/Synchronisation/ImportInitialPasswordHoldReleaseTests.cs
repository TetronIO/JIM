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
/// A change held until its Initial Password is delivered (#2039) is judged at every import of its Connected System,
/// whether or not the import returns the account. Reconciliation otherwise runs only over the objects an import
/// returned and diffed: a Full Import skips an unchanged account by its content hash (the confirming import stamped
/// the disabled state it holds through the hold), and a Delta Import never returns an unchanged account. Without the
/// sweep, a password that then expires or is cancelled (a change to the password's status, not the account), or is
/// delivered without enabling the account, left the change held for ever: the account stayed disabled, nothing was
/// reported, and Drift Detection skipped the object permanently.
/// <para>
/// Drives two or three real <see cref="SyncImportTaskProcessor.PerformImportAsync"/> runs against one in-memory
/// repository, as <c>ImportContentHashSkipTests</c> does. <c>EMPLOYEE_NUMBER</c> stands in for
/// <c>userAccountControl</c>: the account holds the disabled value, the held change asserts the enabled one.
/// </para>
/// </summary>
[TestFixture]
public class ImportInitialPasswordHoldReleaseTests
{
    private const int Enabled = 512;
    private const int Disabled = 514;

    #region accessors
    private MetaverseObject InitiatedBy { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
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
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        var activitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(activitiesData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(ConnectedSystemsData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(ConnectedSystemObjectTypesData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(ConnectedSystemRunProfilesData.BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(TestUtilities.GetConnectedSystemPartitionData().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(TestUtilities.GetServiceSettingsData().BuildMockDbSet().Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(new List<PendingExport>().BuildMockDbSet().Object);

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

        SyncRepo = TestUtilities.CreateSyncRepository(activity: activitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    /// <summary>
    /// The case the sweep exists for: the confirming Full Import held the change while the password was owed, the
    /// password then expired or was cancelled, and the next Full Import skips the unchanged account by its hash. The
    /// change is released all the same, so the next export writes the enabled value as an ordinary Update.
    /// </summary>
    [TestCase(PendingPasswordChangeStatus.Expired)]
    [TestCase(PendingPasswordChangeStatus.Cancelled)]
    public async Task FullImport_HeldChangeOnAnUnchangedAccountWhosePasswordIsNoLongerOwed_ReleasesItAsync(PendingPasswordChangeStatus passwordStatus)
    {
        var hold = await ArrangeHeldDuringAFullImportAsync();
        hold.Password.Status = passwordStatus;

        var skipped = await RunFullImportAsync(ImportObject(hold.HrId, "Jane Smith (renamed)", Disabled));

        var pendingExport = SyncRepo.PendingExports[hold.PendingExportId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(skipped, Is.EqualTo(1), "arrange: the unchanged account was skipped by its hash and never reconciled");
            Assert.That(hold.Change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(hold.Change.AwaitsInitialPassword, Is.False);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Pending), "the released change goes out on the next export");
        }
    }

    /// <summary>
    /// While the password is still owed, the sweep leaves the change exactly as it is.
    /// </summary>
    [Test]
    public async Task FullImport_HeldChangeOnAnUnchangedAccountStillOwedAPassword_KeepsItHeldAsync()
    {
        var hold = await ArrangeHeldDuringAFullImportAsync();

        var skipped = await RunFullImportAsync(ImportObject(hold.HrId, "Jane Smith (renamed)", Disabled));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(skipped, Is.EqualTo(1), "arrange: the unchanged account was skipped by its hash");
            Assert.That(hold.Change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(hold.Change.AwaitsInitialPassword, Is.True);
            Assert.That(SyncRepo.PendingExports[hold.PendingExportId].Status, Is.EqualTo(PendingExportStatus.Exported));
        }
    }

    /// <summary>
    /// A Delta Import returns only what changed, so an account whose password has stopped being owed is never returned
    /// at all. The change is released anyway.
    /// </summary>
    [Test]
    public async Task DeltaImport_HeldChangeOnAnAccountTheImportDoesNotReturn_ReleasesItOnceNoPasswordIsOwedAsync()
    {
        var hold = await ArrangeHeldAccountAsync();
        hold.Password.Status = PendingPasswordChangeStatus.Expired;

        await RunDeltaImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hold.Change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(hold.Change.AwaitsInitialPassword, Is.False);
            Assert.That(SyncRepo.PendingExports[hold.PendingExportId].Status, Is.EqualTo(PendingExportStatus.Pending));
        }
    }

    [Test]
    public async Task DeltaImport_HeldChangeOnAnAccountTheImportDoesNotReturn_KeepsItHeldWhileAPasswordIsOwedAsync()
    {
        var hold = await ArrangeHeldAccountAsync();

        await RunDeltaImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hold.Change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(hold.Change.AwaitsInitialPassword, Is.True);
        }
    }

    /// <summary>
    /// The sweep judges the held change and nothing else: the account was not imported, so the values JIM holds for it
    /// may predate the export, and confirming or retrying another change against them would be a guess. An account no
    /// longer joined to a Metaverse Object is owed nothing, so its change is released without a lookup.
    /// </summary>
    [Test]
    public async Task DeltaImport_ReleasingAHeldChange_LeavesEveryOtherChangeOnTheExportAsItWasAsync()
    {
        var hold = await ArrangeHeldAccountAsync();
        hold.Cso.MetaverseObjectId = null;
        var displayNameAttribute = SourceUserAttribute(MockSourceSystemAttributeNames.DISPLAY_NAME);
        var awaitingConfirmation = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = displayNameAttribute.Id,
            Attribute = displayNameAttribute,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "Jane Smith (as exported)",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1
        };
        SyncRepo.PendingExports[hold.PendingExportId].AttributeValueChanges.Add(awaitingConfirmation);

        await RunDeltaImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hold.Change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending), "the held change is released");
            Assert.That(awaitingConfirmation.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "a change the import could not judge is neither confirmed nor retried");
            Assert.That(awaitingConfirmation.ExportAttemptCount, Is.EqualTo(1));
            Assert.That(SyncRepo.GetMetaverseObjectIdsOwedPasswordCallCount, Is.Zero, "an account with no Metaverse Object is owed nothing");
        }
    }

    /// <summary>
    /// A Connected System with no held change pays one lookup for the held set and nothing more.
    /// </summary>
    [Test]
    public async Task DeltaImport_NothingHeld_NeverLooksUpOwedPasswordsAsync()
    {
        var hold = await ArrangeHeldAccountAsync();
        hold.Change.AwaitsInitialPassword = false;

        await RunDeltaImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.GetMetaverseObjectIdsOwedPasswordCallCount, Is.Zero);
            Assert.That(hold.Change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "an ordinary change the import did not return is left for an import that does");
        }
    }

    #region Arrangement

    private sealed record Hold(Guid HrId, ConnectedSystemObject Cso, Guid PendingExportId, PendingExportAttributeValueChange Change, PendingPasswordChange Password);

    /// <summary>
    /// A joined account that holds the disabled value, its Pending Export holding the enabled value until the Initial
    /// Password owed to it is delivered: the state the export and its confirming import leave behind.
    /// </summary>
    private async Task<Hold> ArrangeHeldAccountAsync()
    {
        var hrId = Guid.NewGuid();
        await RunFullImportAsync(ImportObject(hrId, "Jane Smith", Disabled));
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        cso.MetaverseObjectId = Guid.NewGuid();

        var attribute = SourceUserAttribute(MockSourceSystemAttributeNames.EMPLOYEE_NUMBER);
        var held = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            ChangeType = PendingExportAttributeChangeType.Update,
            IntValue = Enabled,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1,
            AwaitsInitialPassword = true
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = cso.ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = [held]
        };
        SyncRepo.SeedPendingExport(pendingExport);

        var password = new PendingPasswordChange
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = cso.MetaverseObjectId.Value,
            ConnectedSystemId = cso.ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            Status = PendingPasswordChangeStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            ActivityId = Guid.NewGuid()
        };
        await SyncRepo.QueuePasswordChangesAsync([password]);

        return new Hold(hrId, cso, pendingExport.Id, held, password);
    }

    /// <summary>
    /// <see cref="ArrangeHeldAccountAsync"/>, then a Full Import that returns the account changed, so it is reconciled
    /// and the change held by the ordinary path; that import stamps the state the next one compares against.
    /// </summary>
    private async Task<Hold> ArrangeHeldDuringAFullImportAsync()
    {
        var hold = await ArrangeHeldAccountAsync();

        var skipped = await RunFullImportAsync(ImportObject(hold.HrId, "Jane Smith (renamed)", Disabled));

        Assert.That(skipped, Is.Zero, "arrange: the changed account is diffed and reconciled");
        Assert.That(hold.Change.AwaitsInitialPassword && hold.Change.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            Is.True, "arrange: the confirming Full Import held the change while the password is owed");
        return hold;
    }

    private ConnectedSystemObjectTypeAttribute SourceUserAttribute(MockSourceSystemAttributeNames name) =>
        ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER").Attributes.Single(a => a.Name == name.ToString());

    private static ConnectedSystemImportObject ImportObject(Guid hrId, string displayName, int employeeNumber) => new()
    {
        ChangeType = ObjectChangeType.NotSet,
        ObjectType = "SOURCE_USER",
        Attributes =
        [
            new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = [hrId] },
            new() { Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(), StringValues = [displayName] },
            new() { Name = MockSourceSystemAttributeNames.EMPLOYEE_NUMBER.ToString(), IntValues = [employeeNumber] }
        ]
    };

    private static readonly System.Reflection.FieldInfo HashSkippedCountField = typeof(SyncImportTaskProcessor)
        .GetField("_hashSkippedCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?? throw new InvalidOperationException("_hashSkippedCount field not found via reflection - has it been renamed?");

    /// <summary>
    /// Runs a Full Import returning <paramref name="importObject"/>, and returns how many objects it skipped by hash.
    /// </summary>
    private async Task<int> RunFullImportAsync(ConnectedSystemImportObject importObject)
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var runProfile = ConnectedSystemRunProfilesData.Single(
            q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var connector = new MockFileConnector();
        connector.TestImportObjects.Add(importObject);

        var processor = NewProcessor(connector, connectedSystem!, runProfile);
        await processor.PerformImportAsync();
        return (int)HashSkippedCountField.GetValue(processor)!;
    }

    /// <summary>
    /// Runs a Delta Import that returns nothing, as one does when no object changed.
    /// </summary>
    private async Task RunDeltaImportAsync()
    {
        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = 999, Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport, ConnectedSystemId = connectedSystem!.Id
        };

        await NewProcessor(new MockFileConnector(), connectedSystem, runProfile).PerformImportAsync();
    }

    private SyncImportTaskProcessor NewProcessor(MockFileConnector connector, ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
    {
        var activity = new Activity { Id = Guid.NewGuid(), RunProfileExecutionItems = [] };
        return new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy),
            new CancellationTokenSource());
    }

    #endregion
}
