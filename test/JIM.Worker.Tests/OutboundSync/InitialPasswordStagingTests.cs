// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Staging of a first password during export execution (#1121, #1697): a Create that provisions an account whose
/// Synchronisation Rule asks for an initial password writes one row onto the queued password pipeline
/// (<see cref="PendingPasswordChange"/>, <see cref="PendingPasswordChangeOrigin.Provisioned"/>) plus a parent
/// Activity.
/// <para>
/// The behaviour under test is a containment rule as much as a feature. An account that has been created in a
/// Connected System is created; nothing about its password can be allowed to alter that record. Half of these
/// tests therefore assert what does <i>not</i> happen to the export.
/// </para>
/// </summary>
[TestFixture]
public class InitialPasswordStagingTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectTypeAttribute>> MockDbSetConnectedSystemAttributes { get; set; } = null!;
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; } = null!;
    private List<MetaverseObject> MetaverseObjectsData { get; set; } = null!;
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; } = null!;
    private List<SyncRule> SyncRulesData { get; set; } = null!;
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    #endregion

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        var exportRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Target System Export");
        ActivitiesData = TestUtilities.GetActivityData(exportRunProfile.RunType, exportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemAttributesData = ConnectedSystemObjectTypesData.SelectMany(t => t.Attributes).ToList();
        MockDbSetConnectedSystemAttributes = ConnectedSystemAttributesData.BuildMockDbSet();

        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        PendingExportsData = [];
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemAttributes).Returns(MockDbSetConnectedSystemAttributes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);
        // Collision Remediation reads whether Metaverse Object changes are tracked; an empty set falls back to the default.
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(new List<ServiceSetting>().BuildMockDbSet().Object);

        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    /// <summary>
    /// A Create exported by a Synchronisation Rule that asks for an initial password leaves a queued
    /// <see cref="PendingPasswordChangeOrigin.Provisioned"/> change, carrying enough to attempt delivery later:
    /// the account, its Connected System, and the rule whose settings govern the password.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleThatAsksForAPassword_StagesTheAccountAsync()
    {
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(SyncRepo.PendingPasswordChanges, Has.Count.EqualTo(1));

        var staged = SyncRepo.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(staged.ConnectedSystemId, Is.EqualTo(system.Id));
            Assert.That(staged.MetaverseObjectId, Is.EqualTo(cso.MetaverseObjectId));
            Assert.That(staged.SyncRuleId, Is.EqualTo(ProvisioningRuleId));
            Assert.That(staged.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(staged.AttemptCount, Is.Zero, "nothing has been attempted at staging time");
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A provisioned row carries no password of its own: it is generated from the Synchronisation Rule's
    /// initial-password settings at delivery time, not at staging time.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleThatAsksForAPassword_StagesARowWithOriginProvisionedAndNoPasswordAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var staged = SyncRepo.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(staged.EncryptedPassword, Is.Null);
        }
    }

    /// <summary>
    /// The Synchronisation Rule that provisioned the account, not just the account itself, is stamped onto the
    /// row: it is what a delivery attempt reads to resolve the password.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleThatAsksForAPassword_SyncRuleIdIsStampedFromTheProvisioningRuleAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var staged = SyncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(staged.SyncRuleId, Is.EqualTo(ProvisioningRuleId));
    }

    /// <summary>
    /// Staging also writes a parent Activity for the change, already complete: nobody is waiting at a screen for
    /// delivery, which happens on an unrelated later pass.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleThatAsksForAPassword_CreatesACompletedParentActivityNamingTheSystemAsync()
    {
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var staged = SyncRepo.PendingPasswordChanges.Values.Single();
        var activity = SyncRepo.Activities[staged.ActivityId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword));
            Assert.That(activity.TargetContext, Is.EqualTo("Provisioned"));
            Assert.That(activity.TargetName, Is.EqualTo(system.Name));
            Assert.That(activity.MetaverseObjectId, Is.EqualTo(cso.MetaverseObjectId));
            Assert.That(activity.ConnectedSystemId, Is.EqualTo(system.Id));
            Assert.That(activity.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.Message, Does.Contain(system.Name));
        }
    }

    /// <summary>
    /// The staged expiry follows the Connected System's own time to live, so a system known to be going out of
    /// service for longer than a week can be given a window that outlasts the outage.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_SystemWithATimeToLive_StagesTheExpiryAgainstItAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        system.InitialPasswordTimeToLive = TimeSpan.FromDays(30);

        var before = DateTime.UtcNow;
        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());
        var after = DateTime.UtcNow;

        var staged = SyncRepo.PendingPasswordChanges.Values.Single();
        Assert.That(staged.ExpiresAt, Is.InRange(before.AddDays(30), after.AddDays(30)));
    }

    /// <summary>
    /// A Connected System that says nothing keeps the seven days every deployment has had, so upgrading changes
    /// nothing until somebody sets a value.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_SystemWithNoTimeToLive_StagesTheExpiryAgainstTheDefaultAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        var before = DateTime.UtcNow;
        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());
        var after = DateTime.UtcNow;

        var staged = SyncRepo.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(system.InitialPasswordTimeToLive, Is.Null);
            Assert.That(staged.ExpiresAt, Is.InRange(
                before.Add(PendingPasswordChange.DefaultTimeToLive),
                after.Add(PendingPasswordChange.DefaultTimeToLive)));
        }
    }

    /// <summary>
    /// A Synchronisation Rule that provisions but does not ask for an initial password stages nothing at all.
    /// <para>
    /// This is what keeps the work list proportional to the deployments using the feature rather than to how
    /// many accounts JIM has ever created: a system provisioning a hundred thousand accounts writes no rows.
    /// </para>
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleWithNoInitialPassword_StagesNothingAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: false);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(SyncRepo.PendingPasswordChanges, Is.Empty);
        Assert.That(result.InitialPasswordsStagedCount, Is.Zero);
    }

    /// <summary>
    /// An Update to an existing account stages nothing. Only provisioning creates an account that has never
    /// had a password; changing one that already exists must never reset it.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateExport_StagesNothingAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        export.ChangeType = PendingExportChangeType.Update;

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(SyncRepo.PendingPasswordChanges, Is.Empty);
    }

    /// <summary>
    /// A Create the Connected System rejected stages nothing: there is no account to give a password to.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_FailedCreate_StagesNothingAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Failed("The directory refused the object."));

        Assert.That(result.FailedCount, Is.EqualTo(1));
        Assert.That(SyncRepo.PendingPasswordChanges, Is.Empty);
    }

    /// <summary>
    /// The load-bearing one: when staging itself fails, the export that created the account stays successful.
    /// <para>
    /// Marking it failed would have JIM retry the Create, which either duplicates the object in the Connected
    /// System or errors for ever, and would report a provisioning run that worked as one that did not. The
    /// failure is counted instead, so an administrator can still see that the accounts are owed passwords
    /// nobody has recorded.
    /// </para>
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenStagingFails_TheExportStaysSuccessfulAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        SyncRepo.FailProvisionedPasswordStagingWith = new InvalidOperationException("The database rejected the work list row.");

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1), "the account was created; the export must still say so");
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(result.ProcessedExportItems.Single().Succeeded, Is.True);
            Assert.That(result.InitialPasswordStagingFailedCount, Is.EqualTo(1),
                "contained, but never silent: the Activity has to be able to report it");
            Assert.That(result.InitialPasswordsStagedCount, Is.Zero);
        }
    }

    /// <summary>
    /// The passwords were queued and will be delivered; only the Activities recording that could not be written. That
    /// is not a password nobody knows is owed, so it is not counted as one, which would tell an administrator to set
    /// passwords by hand that are already on their way.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenOnlyTheStagingActivitiesFail_ThePasswordIsNotCountedAsUnrecordedAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        SyncRepo.FailActivityCreationWith = new InvalidOperationException("The database rejected the Activity.");

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingPasswordChanges, Has.Count.EqualTo(1), "the password is queued");
            Assert.That(result.InitialPasswordStagingFailedCount, Is.Zero);
        }
    }

    /// <summary>
    /// A Create that came from no Synchronisation Rule (a Pending Export staged before the provisioning rule
    /// was recorded, or by an administrator directly) stages nothing rather than guessing at a rule.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateWithNoProvisioningRule_StagesNothingAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        export.ProvisioningSyncRuleId = null;

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(SyncRepo.PendingPasswordChanges, Is.Empty);
    }

    /// <summary>
    /// One batch, two Synchronisation Rules, only one of which asks for a password: only that rule's account is
    /// staged.
    /// <para>
    /// Added because mutation testing showed the per-account filter could be deleted without any test noticing;
    /// a batch drawn from a single rule is decided entirely by the "does any rule ask for one?" check above it.
    /// A Connected System provisioned by several rules is ordinary, and getting this wrong would reset passwords
    /// on accounts whose rule deliberately does not manage them.
    /// </para>
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_BatchSpanningRules_StagesOnlyTheRuleThatAsksForAPasswordAsync()
    {
        var (system, passwordCso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        var (_, otherCso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: false, syncRuleId: OtherProvisioningRuleId);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(result.SuccessCount, Is.EqualTo(2));
        Assert.That(SyncRepo.PendingPasswordChanges, Has.Count.EqualTo(1));

        var staged = SyncRepo.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.ConnectedSystemObjectId, Is.EqualTo(passwordCso.Id));
            Assert.That(staged.ConnectedSystemObjectId, Is.Not.EqualTo(otherCso.Id));
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A provisioned account with no Metaverse Object cannot be queued at all: the queue's coalescing key is
    /// (Metaverse Object, Connected System), so nothing could ever find such a row again to deliver, supersede,
    /// or expire it. Counted as a staging failure rather than silently dropped.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_AccountWithNoMetaverseObject_CountsAsAStagingFailureAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, withMetaverseObject: false);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1), "the account was created; nothing about its password can alter that");
            Assert.That(SyncRepo.PendingPasswordChanges, Is.Empty);
            Assert.That(result.InitialPasswordStagingFailedCount, Is.EqualTo(1));
            Assert.That(result.InitialPasswordsStagedCount, Is.Zero);
        }
    }

    #region a password change already waiting for the account (#2160)

    /// <summary>
    /// The reported bug: the person's password change was waiting for the account to exist when JIM provisioned it.
    /// The change is the account's first password; it keeps its own password and Activity, and now carries the Initial
    /// Password's promises about the account (the account, the rule, the Create's mark), and is due at once. The export
    /// says so on an Activity of its own, rather than recording nothing.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExistingPendingPropagatedRow_CarriesTheInitialPasswordAndRecordsAnActivityAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: true);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Pending,
            nextRetryAt: DateTime.UtcNow.AddMinutes(30));

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var row = SyncRepo.PendingPasswordChanges.Values.Single();
        var note = SyncRepo.Activities.Values.SingleOrDefault(a => a.TargetType == ActivityTargetType.PasswordSynchronisation
                                                                     && a.TargetContext == nameof(PendingPasswordChangeOrigin.Provisioned));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Id, Is.EqualTo(existing.Id), "the waiting change survives, not replaced");
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(row.EncryptedPassword, Is.EqualTo("encrypted-value"), "the person's own password is delivered");
            Assert.That(row.ActivityId, Is.EqualTo(existing.ActivityId));
            Assert.That(row.ProvisionedConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(row.SyncRuleId, Is.EqualTo(ProvisioningRuleId));
            Assert.That(row.NextRetryAt, Is.Null, "due on the very next delivery pass");
            Assert.That(result.InitialPasswordsCarriedCount, Is.EqualTo(1));
            Assert.That(result.InitialPasswordsStagedCount, Is.Zero);
            Assert.That(note, Is.Not.Null, "the export says what became of the Initial Password");
            Assert.That(note!.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(note.TargetContext, Is.EqualTo(nameof(PendingPasswordChangeOrigin.Provisioned)));
            Assert.That(note.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(note.MetaverseObjectId, Is.EqualTo(metaverseObjectId));
            Assert.That(note.Message, Does.Contain("first password"));
        }
    }

    /// <summary>
    /// A change JIM had stopped trying because the account did not exist yet is released by the Create that provisions
    /// it, its attempts reset; the Activity says it was parked and is due again.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExistingRowParkedForAMissingAccount_IsReleasedAndCarriesTheInitialPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: true);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Parked,
            failureReason: PasswordSetFailureReason.TargetObjectNotFound, attemptCount: 6);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var row = SyncRepo.PendingPasswordChanges[existing.Id];
        var note = NewestProvisionedActivity();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(row.AttemptCount, Is.Zero);
            Assert.That(row.ProvisionedConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(result.InitialPasswordsCarriedCount, Is.EqualTo(1));
            Assert.That(note.Message, Does.Contain("parked"));
        }
    }

    /// <summary>
    /// Decision (#2160): a change held because Password Synchronisation is switched off on the system has not reached
    /// it, and would not while the account's first password is delivered whatever the pause. The Initial Password wins:
    /// it replaces the held change, says so as a warning, and the held change's own record is told it was not set there.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExistingHeldPropagatedRow_IsReplacedByTheInitialPasswordAndSaysSoAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: false);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Pending);
        // Read now: the in-memory queue holds the seeded instance itself, which the takeover rewrites.
        var heldChangeActivityId = existing.ActivityId;

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var row = SyncRepo.PendingPasswordChanges.Values.Single();
        var queued = SyncRepo.Activities[row.ActivityId];
        var toldTheHeldChange = SyncRepo.Activities.Values.SingleOrDefault(a => a.ParentActivityId == heldChangeActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(row.EncryptedPassword, Is.Null);
            Assert.That(row.ProvisionedConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1));
            Assert.That(queued.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(queued.Message, Does.Contain("switched off"));
            Assert.That(toldTheHeldChange, Is.Not.Null, "the person's change must not silently lose this system");
            Assert.That(toldTheHeldChange!.Status, Is.EqualTo(ActivityStatus.CompleteWithError));
            Assert.That(toldTheHeldChange.ConnectedSystemId, Is.EqualTo(system.Id));
        }
    }

    /// <summary>
    /// A change parked for a reason no attempt can cure (here, the directory refused the person's password under its
    /// policy) would leave the new account with no password at all. The Initial Password replaces it, with the
    /// target's words on the warning.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExistingRowParkedForAPolicyRejection_IsReplacedByTheInitialPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: true);
        await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Parked,
            failureReason: PasswordSetFailureReason.PolicyRejection, targetMessage: "The password does not meet the length requirement.");

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var row = SyncRepo.PendingPasswordChanges.Values.Single();
        var queued = SyncRepo.Activities[row.ActivityId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(queued.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(queued.Message, Does.Contain("The password does not meet the length requirement."));
        }
    }

    /// <summary>
    /// An administrator's set for this very account, made before the Create landed, is the account's first password.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExplicitRowNamingTheAccount_CarriesTheInitialPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: false);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Explicit, PendingPasswordChangeStatus.Pending, connectedSystemObjectId: cso.Id);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var row = SyncRepo.PendingPasswordChanges[existing.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit),
                "an administrator's set is never held by a paused system, so it carries the first password");
            Assert.That(row.ProvisionedConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(result.InitialPasswordsCarriedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// An administrator's set whose named account has since been deleted (the foreign key nulled it) can deliver
    /// nothing: the Initial Password replaces it.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExplicitRowWhoseAccountWasDeleted_IsReplacedByTheInitialPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: true);
        await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Explicit, PendingPasswordChangeStatus.Pending, connectedSystemObjectId: null);

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(SyncRepo.PendingPasswordChanges.Values.Single().Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
    }

    /// <summary>
    /// An administrator's set naming another account in the system: that account cannot still be the person's (a person
    /// has at most one joined Connected System Object per Connected System, and the new one is joined to them), so it has
    /// nothing to go to. The Initial Password replaces it, and the person's change records that it was not set there.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExplicitRowForADisjoinedAccount_IsReplacedByTheInitialPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: true);
        var disjoined = SeedDisjoinedAccount(system, cso);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Explicit, PendingPasswordChangeStatus.Pending, connectedSystemObjectId: disjoined.Id);
        var replacedChangeActivityId = existing.ActivityId;

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var row = SyncRepo.PendingPasswordChanges.Values.Single();
        var note = NewestProvisionedActivity();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1));
            Assert.That(note.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(SyncRepo.Activities.Values.Any(a => a.ParentActivityId == replacedChangeActivityId), Is.True);
        }
    }

    /// <summary>
    /// An Initial Password still owed to an earlier account of the person that has since been disjoined: the new
    /// account's replaces it, owing the new account.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_LiveInitialPasswordForADisjoinedAccount_IsSupersededAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: true);
        var disjoined = SeedDisjoinedAccount(system, cso);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Pending, connectedSystemObjectId: disjoined.Id);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var row = SyncRepo.PendingPasswordChanges[existing.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(row.ProvisionedConnectedSystemObjectId, Is.EqualTo(cso.Id));
        }
    }

    /// <summary>
    /// The person's own live change is for the Object Type the system's Password Synchronisation sets passwords on, which
    /// the new account is not, so it cannot reach it, nor any other account of theirs there while this one is joined to
    /// them. The Initial Password replaces it with a warning, and the person's change records that it was not set there.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_PropagatedRowForAnotherObjectType_IsReplacedByTheInitialPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: true);
        system.PasswordSynchronisation!.TargetObjectTypeId = cso.TypeId + 1;
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Pending);
        var replacedChangeActivityId = existing.ActivityId;

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        var row = SyncRepo.PendingPasswordChanges.Values.Single();
        var note = NewestProvisionedActivity();
        var notSet = SyncRepo.Activities.Values.SingleOrDefault(a => a.ParentActivityId == replacedChangeActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Id, Is.EqualTo(existing.Id));
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1));
            Assert.That(result.InitialPasswordStagingFailedCount, Is.Zero);
            Assert.That(note.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(notSet?.Status, Is.EqualTo(ActivityStatus.CompleteWithError));
        }
    }

    /// <summary>
    /// The Create wrote the account's flags (an Attribute Flow sets userAccountControl), so the waiting change carries
    /// that mark, and its delivery leaves both flags to the flow.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateReportingItWroteTheEnabledState_CarriesTheMarkOntoTheWaitingRowAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        ArrangePasswordSynchronisation(system, cso, enabled: true);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Pending);

        await ExportAsync(system, SucceededWritingTheEnabledState());

        Assert.That(SyncRepo.PendingPasswordChanges[existing.Id].EnabledStateSetByAttributeFlow, Is.True);
    }

    /// <summary>
    /// A leaver disabled before the waiting change landed: the Update that wrote the enabled state marks the change
    /// carrying the account's first password, so its delivery never re-enables the leaver on the rule's say-so.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateReportingItWroteTheEnabledState_MarksAWaitingRowCarryingTheInitialPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        export.ChangeType = PendingExportChangeType.Update;
        var carrying = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Pending, connectedSystemObjectId: cso.Id);
        SyncRepo.PendingPasswordChanges[carrying.Id].CarryInitialPassword(cso.Id, ProvisioningRuleId, false, DateTime.UtcNow.AddDays(7));

        await ExportAsync(system, SucceededWritingTheEnabledState());

        Assert.That(SyncRepo.PendingPasswordChanges[carrying.Id].EnabledStateSetByAttributeFlow, Is.True);
    }

    #endregion

    /// <summary>
    /// An existing Expired row carries a dead password: it must not block the account's first one, so the
    /// provisioned change takes it over in place.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ExistingExpiredRow_IsSupersededAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Expired);

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(SyncRepo.PendingPasswordChanges, Has.Count.EqualTo(1));
        var row = SyncRepo.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Id, Is.EqualTo(existing.Id), "the row now in the table keeps its own id");
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(row.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(SyncRepo.Activities, Contains.Key(row.ActivityId));
        }
    }

    /// <summary>
    /// An account deleted and re-provisioned leaves behind a Provisioned row from its first life; the new one
    /// must win, because the old account and its password no longer exist.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ReprovisionedAccount_SupersedesTheOldProvisionedRowAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        var existing = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Parked,
            connectedSystemObjectId: Guid.NewGuid());

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(SyncRepo.PendingPasswordChanges, Has.Count.EqualTo(1));
        var row = SyncRepo.PendingPasswordChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Id, Is.EqualTo(existing.Id));
            Assert.That(row.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id), "the new account, not the deleted one");
        }
    }

    #region Whether an export wrote the account's enabled state (#2064)

    /// <summary>
    /// A Create whose Connector reports writing the account's enabled state (an Attribute Flow sets
    /// <c>userAccountControl</c>) stages the first password marked, so its delivery leaves that state to the flow: a
    /// pre-hire the flow creates disabled is not enabled with a password JIM knows.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateReportingItWroteTheEnabledState_StagesTheRowMarkedAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        await ExportAsync(system, SucceededWritingTheEnabledState());

        Assert.That(SyncRepo.PendingPasswordChanges.Values.Single().EnabledStateSetByAttributeFlow, Is.True);
    }

    [Test]
    public async Task ExecuteExportsAsync_CreateNotReportingIt_StagesTheRowUnmarkedAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(SyncRepo.PendingPasswordChanges.Values.Single().EnabledStateSetByAttributeFlow, Is.False,
            "nothing sets the enabled state, so the rule's Enable Account decides");
    }

    /// <summary>
    /// An Update that writes the enabled state of an account still owed its first password (a leaver disabled before
    /// the password landed) marks that password, so a late delivery never re-enables the account.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateReportingItWroteTheEnabledState_MarksTheAccountsOwedFirstPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        export.ChangeType = PendingExportChangeType.Update;
        var owed = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Parked, connectedSystemObjectId: cso.Id);

        var result = await ExportAsync(system, SucceededWritingTheEnabledState());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(SyncRepo.PendingPasswordChanges[owed.Id].EnabledStateSetByAttributeFlow, Is.True);
        }
    }

    /// <summary>
    /// A first password an administrator cancelled is not dead: a Retry puts it back on the queue. So an export that
    /// writes the account's enabled state marks it too, or the retried delivery would re-enable a leaver.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateReportingItWroteTheEnabledState_MarksACancelledFirstPasswordTooAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        export.ChangeType = PendingExportChangeType.Update;
        var cancelled = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Cancelled, connectedSystemObjectId: cso.Id);

        await ExportAsync(system, SucceededWritingTheEnabledState());

        Assert.That(SyncRepo.PendingPasswordChanges[cancelled.Id].EnabledStateSetByAttributeFlow, Is.True);
    }

    [Test]
    public async Task ExecuteExportsAsync_UpdateNotReportingIt_LeavesTheOwedFirstPasswordUnmarkedAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        export.ChangeType = PendingExportChangeType.Update;
        var owed = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Pending, connectedSystemObjectId: cso.Id);

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(SyncRepo.PendingPasswordChanges[owed.Id].EnabledStateSetByAttributeFlow, Is.False);
    }

    /// <summary>
    /// Recording the mark is contained like staging is: the directory has already changed, so the export stays
    /// successful, and the failure is logged and counted rather than passing quietly.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenMarkingFails_TheExportStaysSuccessfulAndTheFailureIsCountedAsync()
    {
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        export.ChangeType = PendingExportChangeType.Update;
        SyncRepo.FailInitialPasswordMarkingWith = new InvalidOperationException("The database refused the mark.");

        var result = await ExportAsync(system, SucceededWritingTheEnabledState());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(result.InitialPasswordMarkingFailedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A delivery waits while the account has an export in flight (#2173), so the mark has to be written while the
    /// export still is in flight: written after its Pending Export left <see cref="PendingExportStatus.Executing"/>, it
    /// leaves a moment where the delivery reads neither the export nor the mark and enables the leaver.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateReportingItWroteTheEnabledState_MarksBeforeTheExportLeavesExecutingAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        export.ChangeType = PendingExportChangeType.Update;
        var owed = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Pending, connectedSystemObjectId: cso.Id);
        var markedWhenTheStatusWasWritten = new List<bool>();
        SyncRepo.OnPendingExportsUpdated = ids =>
        {
            if (ids.Contains(export.Id))
                markedWhenTheStatusWasWritten.Add(SyncRepo.PendingPasswordChanges[owed.Id].EnabledStateSetByAttributeFlow);
        };

        await ExportAsync(system, SucceededWritingTheEnabledState());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(markedWhenTheStatusWasWritten, Is.Not.Empty, "the export's status was written");
            Assert.That(markedWhenTheStatusWasWritten, Has.All.True,
                "the mark must be in place before the export stops counting as in flight");
        }
    }

    /// <summary>
    /// A password change waiting on the account waits only while its Create is in flight (#2160), and is delivered and
    /// removed the moment it is not. The Create therefore stays in flight until its Initial Password is queued (#2270):
    /// were its Pending Export written first, a change delivered in between would be gone by the time staging looks for
    /// it, and the Initial Password queued in its place would overwrite the person's own password.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleThatAsksForAPassword_QueuesTheInitialPasswordBeforeTheCreateLeavesExecutingAsync()
    {
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        var queuedWhenTheStatusWasWritten = new List<bool>();
        SyncRepo.OnPendingExportsUpdated = ids =>
        {
            if (ids.Contains(export.Id))
                queuedWhenTheStatusWasWritten.Add(SyncRepo.PendingPasswordChanges.Values.Any(c => c.ConnectedSystemObjectId == cso.Id));
        };

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1));
            Assert.That(queuedWhenTheStatusWasWritten, Has.Count.EqualTo(1), "the Create's status is written exactly once");
            Assert.That(queuedWhenTheStatusWasWritten, Has.All.True,
                "the Initial Password must be queued before the Create stops counting as in flight");
        }
    }

    /// <summary>
    /// Holding the Create back must not let it stay Executing when the batch fails afterwards: its success has been
    /// recorded, and a failure that left it Executing would have it put back and sent again, creating the account twice
    /// (#2083, #2270). Here Collision Remediation fails for another change in the batch, after the batch's results are
    /// recorded and before the Initial Password is staged (#2197).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_BatchFailingAfterTheCreateSucceeded_StillWritesTheCreatesStatusAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        var (_, _, rejected) = ArrangeProvisioningCreate(initialPasswordEnabled: false, syncRuleId: ProvisioningRuleId + 1);
        rejected.CreatedAt = export.CreatedAt.AddSeconds(1);
        system.ConnectorDefinition.SupportsUniquenessRejectionClassification = true;
        SyncRepo.FailSyncRuleLoadWith = new InvalidOperationException("The Synchronisation Rules could not be read.");
        var statusWritten = false;
        SyncRepo.OnPendingExportsUpdated = ids => statusWritten |= ids.Contains(export.Id);
        var rejection = ConnectedSystemExportResult.Failed("That value is already in use.");
        rejection.ErrorType = ConnectedSystemExportErrorType.UniqueValueAlreadyInUse;

        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                exports.Select(pe => pe.Id == export.Id ? ConnectedSystemExportResult.Succeeded("CN=Test User") : rejection).ToList());

        var result = await Jim.ExportExecution.ExecuteExportsAsync(system, mockConnector.Object, SyncRunMode.PreviewAndSync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ProcessedExportItems.Single(item => item.PendingExportId == rejected.Id).Succeeded, Is.False,
                "the failure this test depends on happened");
            Assert.That(statusWritten, Is.True, "a Create whose success was recorded must leave Executing whatever fails after it");
        }
    }

    /// <summary>
    /// A Delete performed as a disable, against an account whose provisioning no import has confirmed yet, removes the
    /// Connected System Object rather than leaving its export awaiting confirmation. Its owed first password is marked
    /// all the same, before the object goes: a delivery in between would otherwise enable the account it disabled.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_DeleteDisablingAnUnconfirmedProvisionedAccount_MarksItsOwedFirstPasswordAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        export.ChangeType = PendingExportChangeType.Delete;
        cso.Status = ConnectedSystemObjectStatus.PendingProvisioning;
        var owed = await SeedExistingChangeAsync(metaverseObjectId, system.Id,
            PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Pending, connectedSystemObjectId: cso.Id);

        await ExportAsync(system, SucceededWritingTheEnabledState());

        Assert.That(SyncRepo.PendingPasswordChanges[owed.Id].EnabledStateSetByAttributeFlow, Is.True);
    }

    private static ConnectedSystemExportResult SucceededWritingTheEnabledState()
    {
        var result = ConnectedSystemExportResult.Succeeded();
        result.SetsAccountEnabledState = true;
        return result;
    }

    #endregion

    #region An Initial Password follows the Create (#2039)

    /// <summary>
    /// A Create whose provisioning Synchronisation Rule asks for an Initial Password reaches the Connector flagged, so
    /// a directory that refuses an enabled account with no password (Active Directory) can create it disabled, to be
    /// enabled once the password is set.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleThatAsksForAPassword_IsHandedToTheConnectorFlaggedAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        var (_, handedToConnector) = await ExportCapturingAsync(system);

        Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.True);
    }

    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleWithNoInitialPassword_IsHandedToTheConnectorUnflaggedAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: false);

        var (_, handedToConnector) = await ExportCapturingAsync(system);

        Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.False);
    }

    /// <summary>
    /// An Update is flagged from what is owed, never from the rule: the rule's Initial Password follows a Create, and an
    /// Update to an account owed nothing on the Connected System is sent unflagged, whatever its rule asks for (#2158).
    /// Staging nothing for it is exactly what <see cref="ExecuteExportsAsync_UpdateExport_StagesNothingAsync"/> pins.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateFromARuleThatAsksForAPassword_IsHandedToTheConnectorUnflaggedAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        export.ChangeType = PendingExportChangeType.Update;

        var (_, handedToConnector) = await ExportCapturingAsync(system, setsPasswords: true);

        Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.False);
    }

    [Test]
    public async Task ExecuteExportsAsync_CreateWithNoProvisioningRule_IsHandedToTheConnectorUnflaggedAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        export.ProvisioningSyncRuleId = null;

        var (_, handedToConnector) = await ExportCapturingAsync(system);

        Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.False);
    }

    [Test]
    public async Task ExecuteExportsAsync_BatchSpanningRules_FlagsOnlyTheCreateWhoseRuleAsksForAPasswordAsync()
    {
        var (system, _, passwordExport) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        var (_, _, otherExport) = ArrangeProvisioningCreate(initialPasswordEnabled: false, syncRuleId: OtherProvisioningRuleId);

        var (_, handedToConnector) = await ExportCapturingAsync(system);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedToConnector.Single(pe => pe.Id == passwordExport.Id).InitialPasswordFollows, Is.True);
            Assert.That(handedToConnector.Single(pe => pe.Id == otherExport.Id).InitialPasswordFollows, Is.False);
        }
    }

    /// <summary>
    /// The lookup that flags the Create is the one that stages its password: it moved ahead of the Connector call
    /// rather than being repeated, so a provisioning run costs no more queries than it did before (#2039).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleThatAsksForAPassword_LooksUpTheRulesOnceForTheBatchAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        ArrangeProvisioningCreate(initialPasswordEnabled: false, syncRuleId: OtherProvisioningRuleId);

        var (result, _) = await ExportCapturingAsync(system);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.GetSyncRuleIdsWithInitialPasswordEnabledCallCount, Is.EqualTo(1));
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1), "the one lookup still decides what is staged");
        }
    }

    [Test]
    public async Task ExecuteExportsAsync_TwoBatches_LooksUpTheRulesOncePerBatchAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        ArrangeProvisioningCreate(initialPasswordEnabled: true);

        var (result, handedToConnector) = await ExportCapturingAsync(system, new ExportExecutionOptions { BatchSize = 1 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedToConnector, Has.Count.EqualTo(2), "precondition: one Create per batch");
            Assert.That(SyncRepo.GetSyncRuleIdsWithInitialPasswordEnabledCallCount, Is.EqualTo(2));
            Assert.That(handedToConnector.Select(pe => pe.InitialPasswordFollows), Is.All.True);
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(2));
        }
    }

    /// <summary>
    /// A batch with no Create from a provisioning Synchronisation Rule has nothing to decide, so nothing is read: a
    /// run of Updates costs no query at all.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_NoCreateFromAProvisioningRule_DoesNotLookUpTheRulesAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        export.ChangeType = PendingExportChangeType.Update;

        await ExportCapturingAsync(system);

        Assert.That(SyncRepo.GetSyncRuleIdsWithInitialPasswordEnabledCallCount, Is.Zero);
    }

    /// <summary>
    /// A failed lookup is contained, as it was when only staging made it: the Create is still sent, as its Attribute
    /// Flows say, the account it creates stays created, and the password nobody could record is counted for the
    /// Activity to report.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenTheRuleLookupFails_SendsTheCreateUnflaggedAndCountsTheUnrecordedPasswordAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        SyncRepo.FailInitialPasswordRuleLookupWith = new InvalidOperationException("The database could not be reached.");

        var (result, handedToConnector) = await ExportCapturingAsync(system);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.False,
                "JIM cannot tell a password will follow, so it does not claim one will");
            Assert.That(result.SuccessCount, Is.EqualTo(1), "the account was created; the export must still say so");
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(SyncRepo.PendingPasswordChanges, Is.Empty);
            Assert.That(result.InitialPasswordStagingFailedCount, Is.EqualTo(1), "contained, but never silent");
            Assert.That(SyncRepo.GetSyncRuleIdsWithInitialPasswordEnabledCallCount, Is.EqualTo(1), "a failed lookup is not repeated by staging");
        }
    }

    /// <summary>
    /// A Create written in part (#1398) reaches the Connector as a copy carrying only what it can write now; the copy
    /// must still say an Initial Password follows, or the account would be created enabled after all.
    /// </summary>
    [Test]
    public void ForConnector_CopyForAPartialWrite_KeepsInitialPasswordFollows()
    {
        var (_, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportAttributeChangeType.Add,
            AttributeId = export.AttributeValueChanges[0].AttributeId,
            Attribute = export.AttributeValueChanges[0].Attribute,
            UnresolvedReferenceValue = Guid.NewGuid().ToString(),
            Status = PendingExportAttributeChangeStatus.Pending
        });
        export.InitialPasswordFollows = true;

        var handedToConnector = JIM.Application.Servers.ExportExecutionServer.ForConnector(export);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedToConnector, Is.Not.SameAs(export), "precondition: the Connector is handed a copy");
            Assert.That(handedToConnector.InitialPasswordFollows, Is.True);
        }
    }

    #endregion

    #region A password follows an Update to an account still owed one (#2158)

    /// <summary>
    /// An Update to an account whose person is still owed a password on the Connected System reaches the Connector
    /// flagged, whatever the password's origin, so a target that refuses to enable an account with no password (Active
    /// Directory) can hold the enable for the delivery to complete. The test is the import hold's own: queued, retrying
    /// or parked.
    /// </summary>
    [TestCase(PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Pending)]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Parked)]
    [TestCase(PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Delivering)]
    [TestCase(PendingPasswordChangeOrigin.Propagated, PendingPasswordChangeStatus.Pending)]
    public async Task ExecuteExportsAsync_UpdateToAnAccountOwedAPassword_IsHandedToTheConnectorFlaggedAsync(
        PendingPasswordChangeOrigin origin, PendingPasswordChangeStatus status)
    {
        var (system, export, _) = await ArrangeUpdateToAnAccountOwedAsync(origin, status);

        var (_, handedToConnector) = await ExportCapturingAsync(system, setsPasswords: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.True);
            Assert.That(SyncRepo.GetMetaverseObjectIdsOwedPasswordCallCount, Is.EqualTo(1), "one lookup for the batch");
        }
    }

    /// <summary>
    /// An expired or cancelled password will never be delivered, so nothing would complete a held enable: the Update is
    /// sent as its Attribute Flow asks, and a target that refuses it reports the refusal with its remedies.
    /// </summary>
    [TestCase(PendingPasswordChangeStatus.Expired)]
    [TestCase(PendingPasswordChangeStatus.Cancelled)]
    public async Task ExecuteExportsAsync_UpdateToAnAccountWhosePasswordExpiredOrWasCancelled_IsHandedToTheConnectorUnflaggedAsync(
        PendingPasswordChangeStatus status)
    {
        var (system, export, _) = await ArrangeUpdateToAnAccountOwedAsync(PendingPasswordChangeOrigin.Provisioned, status);

        var (_, handedToConnector) = await ExportCapturingAsync(system, setsPasswords: true);

        Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.False);
    }

    [Test]
    public async Task ExecuteExportsAsync_UpdateToAnObjectWithNoMetaverseObject_IsHandedToTheConnectorUnflaggedAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true, withMetaverseObject: false);
        export.ChangeType = PendingExportChangeType.Update;

        var (_, handedToConnector) = await ExportCapturingAsync(system, setsPasswords: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.False);
            Assert.That(SyncRepo.GetMetaverseObjectIdsOwedPasswordCallCount, Is.Zero, "no person, so nothing can be owed");
        }
    }

    /// <summary>
    /// A batch of Creates has no Update to decide, so the owed lookup is never made: a provisioning run costs no more
    /// queries than it did.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_BatchWithNoUpdates_NeverLooksUpOwedPasswordsAsync()
    {
        var (system, _, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        await ExportCapturingAsync(system, setsPasswords: true);

        Assert.That(SyncRepo.GetMetaverseObjectIdsOwedPasswordCallCount, Is.Zero);
    }

    /// <summary>
    /// A Connected System whose Connector cannot set passwords can have no password queued to complete a held enable,
    /// so its Updates are never looked up: an export of a hundred thousand rows to a file or a database costs nothing.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorThatSetsNoPasswords_NeverLooksUpOwedPasswordsAsync()
    {
        var (system, export, _) = await ArrangeUpdateToAnAccountOwedAsync(PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Pending);

        var (_, handedToConnector) = await ExportCapturingAsync(system, setsPasswords: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.False);
            Assert.That(SyncRepo.GetMetaverseObjectIdsOwedPasswordCallCount, Is.Zero);
        }
    }

    /// <summary>
    /// A failed owed lookup is contained, as the rule lookup is: the Update is sent unflagged, so a target that refuses
    /// the enable reports it on the execution item as it would have without the lookup, the batch carries on, and the
    /// failure is logged as a warning naming the Connected System rather than passing quietly.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_WhenTheOwedLookupFails_SendsTheUpdateUnflaggedAndTheExportProceedsAsync()
    {
        var (system, export, _) = await ArrangeUpdateToAnAccountOwedAsync(PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Pending);
        SyncRepo.FailOwedPasswordLookupWith = new InvalidOperationException("The database could not be reached.");
        var sink = new CapturingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        ExportExecutionResult result;
        List<PendingExport> handedToConnector;
        try
        {
            (result, handedToConnector) = await ExportCapturingAsync(system, setsPasswords: true);
        }
        finally
        {
            Log.Logger = original;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedToConnector.Single(pe => pe.Id == export.Id).InitialPasswordFollows, Is.False);
            Assert.That(result.SuccessCount, Is.EqualTo(1), "the export still goes");
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(sink.Events.Any(e => e.Level == LogEventLevel.Warning
                                             && e.Exception is InvalidOperationException
                                             && e.RenderMessage().Contains(system.Name)), Is.True,
                "contained, but never silent: a warning naming the Connected System");
        }
    }

    /// <summary>
    /// A flagged Update the Connector reports written with an interim value is held exactly as a Create is: the change is
    /// marked, so the confirming import waits for the password; no password is staged for it, since one is already
    /// queued; and the value the flow asked for is not recorded on the object as though it had been written.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_UpdateReportedAwaitingInitialPassword_MarksTheChangeStagesNothingAndRecordsNoAssertedValueAsync()
    {
        var (system, export, cso) = await ArrangeUpdateToAnAccountOwedAsync(PendingPasswordChangeOrigin.Provisioned, PendingPasswordChangeStatus.Parked);
        var displayNameChange = export.AttributeValueChanges.Single();
        displayNameChange.ChangeType = PendingExportAttributeChangeType.Update;

        var (result, _) = await ExportCapturingAsync(system, setsPasswords: true,
            answer: _ => ConnectedSystemExportResult.Succeeded(null, null, ["DisplayName"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(displayNameChange.AwaitsInitialPassword, Is.True, "a report on a flagged Update is accepted");
            Assert.That(displayNameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(SyncRepo.PendingPasswordChanges, Has.Count.EqualTo(1), "the queued password is the one that follows; nothing new is staged");
            Assert.That(result.InitialPasswordsStagedCount, Is.Zero);
            Assert.That(cso.AttributeValues.Where(v => v.AttributeId == displayNameChange.AttributeId), Is.Empty,
                "the Connector wrote an interim value, so the asserted one is not recorded as held");
        }
    }

    /// <summary>
    /// Arranges an Update to an account whose person has a queued password change on the Connected System, in the given
    /// origin and status.
    /// </summary>
    private async Task<(ConnectedSystem System, PendingExport Export, ConnectedSystemObject Cso)> ArrangeUpdateToAnAccountOwedAsync(
        PendingPasswordChangeOrigin origin, PendingPasswordChangeStatus status)
    {
        var metaverseObjectId = Guid.NewGuid();
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true, metaverseObjectId: metaverseObjectId);
        export.ChangeType = PendingExportChangeType.Update;
        await SeedExistingChangeAsync(metaverseObjectId, system.Id, origin, status,
            connectedSystemObjectId: origin == PendingPasswordChangeOrigin.Provisioned ? cso.Id : null);
        return (system, export, cso);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }

    #endregion

    #region Attributes written with an interim value (#2039 Phase 2)

    /// <summary>
    /// The Connector names the attributes it wrote with an interim value because an Initial Password follows; each
    /// written change for one of them is marked so the confirming import judges it against the password's delivery
    /// rather than reporting it unconfirmed. Names match case-insensitively, as directory attribute names do.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorReportsAnAttributeAwaitingInitialPassword_MarksThatChangeOnlyAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        var displayNameChange = export.AttributeValueChanges.Single();
        var employeeIdAttribute = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER")
            .Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.EmployeeId.ToString());
        var employeeIdChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportAttributeChangeType.Update,
            AttributeId = employeeIdAttribute.Id,
            Attribute = employeeIdAttribute,
            StringValue = "E123",
            Status = PendingExportAttributeChangeStatus.Pending
        };
        export.AttributeValueChanges.Add(employeeIdChange);

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded(null, null, ["DISPLAYNAME"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(displayNameChange.AwaitsInitialPassword, Is.True);
            Assert.That(displayNameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "the change was sent and awaits its confirming import like any other");
            Assert.That(employeeIdChange.AwaitsInitialPassword, Is.False, "only the attributes the Connector named are marked");
            Assert.That(employeeIdChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    /// <summary>
    /// A Connector may rewrite a value only when JIM said an Initial Password follows. One that reports an interim
    /// value on any other export has broken that contract; JIM logs it and does not mark the change, so the
    /// confirming import treats it as an ordinary change rather than holding it for a password nobody will deliver.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorReportsAnInterimValueWhenNoInitialPasswordFollows_LeavesTheChangeUnmarkedAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: false);
        var displayNameChange = export.AttributeValueChanges.Single();

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded(null, null, ["DisplayName"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1), "the write itself succeeded and is recorded as such");
            Assert.That(displayNameChange.AwaitsInitialPassword, Is.False);
            Assert.That(displayNameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    /// <summary>
    /// The mark describes the write just made, never an earlier one. A change marked on an earlier attempt and sent
    /// again (a Create no import ever confirmed is retried as one) carries the value this Connector wrote now; if it
    /// reported no interim value this time, the change is an ordinary one, or the next import would hold it for a
    /// password that may never come.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ResentChangeMarkedBeforeButNotReportedNow_IsUnmarkedAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        var displayNameChange = export.AttributeValueChanges.Single();
        displayNameChange.Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
        displayNameChange.ExportAttemptCount = 1;
        displayNameChange.AwaitsInitialPassword = true;

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(displayNameChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "precondition: the change was sent again");
            Assert.That(displayNameChange.AwaitsInitialPassword, Is.False);
        }
    }

    /// <summary>
    /// A Connector that rewrote nothing reports nothing, and no change is marked: the path is inert unless a Connector
    /// names an attribute.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_InitialPasswordFollowsButConnectorRewroteNothing_LeavesTheChangeUnmarkedAsync()
    {
        var (system, _, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        var displayNameChange = export.AttributeValueChanges.Single();

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(displayNameChange.AwaitsInitialPassword, Is.False);
    }

    /// <summary>
    /// Optimistic apply records what the Connector wrote, so a change written with an interim value is not recorded as
    /// though the asserted value had been written: the target holds the interim value, and recording the asserted one
    /// would let a later evaluation believe the target already holds it. The attribute is left as it was until the
    /// confirming import reports what the target really holds; every other written change is recorded as usual.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ConnectorReportsAnAttributeAwaitingInitialPassword_DoesNotRecordTheAssertedValueOnTheObjectAsync()
    {
        var (system, cso, export) = ArrangeProvisioningCreate(initialPasswordEnabled: true);
        var displayNameAttributeId = export.AttributeValueChanges.Single().AttributeId;
        var employeeIdAttribute = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER")
            .Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.EmployeeId.ToString());
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportAttributeChangeType.Update,
            AttributeId = employeeIdAttribute.Id,
            Attribute = employeeIdAttribute,
            StringValue = "E123",
            Status = PendingExportAttributeChangeStatus.Pending
        });

        await ExportAsync(system, ConnectedSystemExportResult.Succeeded(null, null, ["DisplayName"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.AttributeValues.Where(v => v.AttributeId == displayNameAttributeId), Is.Empty,
                "the Connector wrote an interim value, so the asserted one is not recorded as held");
            Assert.That(cso.AttributeValues.Where(v => v.AttributeId == employeeIdAttribute.Id).Select(v => v.StringValue),
                Is.EqualTo(new[] { "E123" }), "a change written as asserted is recorded as before");
        }
    }

    #endregion

    #region Helper Methods

    private const int ProvisioningRuleId = 91121;
    private const int OtherProvisioningRuleId = 91122;

    /// <summary>
    /// Sets up one Create Pending Export, provisioned by a Synchronisation Rule that either does or does not
    /// ask for an initial password.
    /// </summary>
    private (ConnectedSystem System, ConnectedSystemObject Cso, PendingExport Export) ArrangeProvisioningCreate(
        bool initialPasswordEnabled,
        int syncRuleId = ProvisioningRuleId,
        bool withMetaverseObject = true,
        Guid? metaverseObjectId = null)
    {
        var system = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var userType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var displayNameAttribute = userType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        var syncRule = new SyncRule
        {
            Id = syncRuleId,
            Name = $"Provision Users ({syncRuleId})",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = system.Id,
            ProvisionToConnectedSystem = true,
            InitialPassword = initialPasswordEnabled ? new SyncRuleInitialPassword { SyncRuleId = syncRuleId, Enabled = true } : null
        };
        SyncRepo.SeedSyncRule(syncRule);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            Type = userType,
            TypeId = userType.Id,
            AttributeValues = [],
            MetaverseObjectId = withMetaverseObject ? metaverseObjectId ?? Guid.NewGuid() : null
        };
        ConnectedSystemObjectsData.Add(cso);

        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystem = system,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Create,
            CreatedAt = DateTime.UtcNow,
            ProvisioningSyncRuleId = syncRuleId,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Add,
                    AttributeId = displayNameAttribute.Id,
                    Attribute = displayNameAttribute,
                    StringValue = "Test User",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            ]
        };
        PendingExportsData.Add(export);
        SyncRepo.SeedPendingExport(export);

        return (system, cso, export);
    }

    /// <summary>
    /// Gives the system a Password Synchronisation configuration targeting the provisioned account's Object Type, on or
    /// off, and makes the account and the system known to the queue's repository, which is where the staging statement
    /// reads both (#2160).
    /// </summary>
    private void ArrangePasswordSynchronisation(ConnectedSystem system, ConnectedSystemObject cso, bool enabled)
    {
        system.PasswordSynchronisation = new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = system.Id,
            Enabled = enabled,
            TargetObjectTypeId = cso.TypeId
        };
        SyncRepo.SeedConnectedSystem(system);
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    /// <summary>
    /// Another account in the same Connected System that is joined to nobody: a person has at most one joined Connected
    /// System Object per Connected System, so an earlier account of theirs alongside the new one is always disjoined.
    /// </summary>
    private ConnectedSystemObject SeedDisjoinedAccount(ConnectedSystem system, ConnectedSystemObject cso)
    {
        var other = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            Type = cso.Type,
            TypeId = cso.TypeId,
            AttributeValues = [],
            MetaverseObjectId = null
        };
        SyncRepo.SeedConnectedSystemObject(other);
        return other;
    }

    /// <summary>The newest Activity export staging recorded about an Initial Password.</summary>
    private Activity NewestProvisionedActivity() =>
        SyncRepo.Activities.Values
            .Where(a => a.TargetType == ActivityTargetType.PasswordSynchronisation
                        && a.TargetContext == nameof(PendingPasswordChangeOrigin.Provisioned))
            .OrderBy(a => a.Created)
            .Last();

    /// <summary>
    /// Seeds a row already on the queue for a (Metaverse Object, Connected System) key a provisioned change is
    /// about to be offered against, so the coalescing tests can arrange what it finds there.
    /// </summary>
    private async Task<PendingPasswordChange> SeedExistingChangeAsync(
        Guid metaverseObjectId,
        int connectedSystemId,
        PendingPasswordChangeOrigin origin,
        PendingPasswordChangeStatus status,
        Guid? connectedSystemObjectId = null,
        DateTime? nextRetryAt = null,
        PasswordSetFailureReason? failureReason = null,
        string? targetMessage = null,
        int attemptCount = 0)
    {
        var existing = new PendingPasswordChange
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = metaverseObjectId,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = connectedSystemObjectId,
            Origin = origin,
            Status = status,
            NextRetryAt = nextRetryAt,
            FailureReason = failureReason,
            TargetMessage = targetMessage,
            AttemptCount = attemptCount,
            EncryptedPassword = origin == PendingPasswordChangeOrigin.Provisioned ? null : "encrypted-value",
            ProvisionedConnectedSystemObjectId = origin == PendingPasswordChangeOrigin.Provisioned ? connectedSystemObjectId : null,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await SyncRepo.QueuePasswordChangesAsync([existing]);
        return existing;
    }

    /// <summary>
    /// Runs an export whose Connector succeeds for everything, recording each Pending Export as the Connector was
    /// handed it (the flag is read there, not on the export the run holds).
    /// </summary>
    /// <param name="setsPasswords">Whether the Connector can set passwords (<see cref="IConnectorPasswordManagement"/>),
    /// as the LDAP Connector can: only then can a password owed to an account follow an Update (#2158).</param>
    /// <param name="answer">The result for each export, success by default.</param>
    private async Task<(ExportExecutionResult Result, List<PendingExport> HandedToConnector)> ExportCapturingAsync(
        ConnectedSystem system,
        ExportExecutionOptions? options = null,
        bool setsPasswords = false,
        Func<PendingExport, ConnectedSystemExportResult>? answer = null)
    {
        var handedToConnector = new List<PendingExport>();
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        if (setsPasswords)
            mockConnector.As<IConnectorPasswordManagement>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
            {
                // Copied as handed over: the flag is read now, before the run moves on.
                handedToConnector.AddRange(exports.Select(pe => new PendingExport { Id = pe.Id, InitialPasswordFollows = pe.InitialPasswordFollows }));
                return exports.Select(pe => answer?.Invoke(pe) ?? ConnectedSystemExportResult.Succeeded()).ToList();
            });

        var result = await Jim.ExportExecution.ExecuteExportsAsync(system, mockConnector.Object, SyncRunMode.PreviewAndSync,
            options, CancellationToken.None);
        return (result, handedToConnector);
    }

    private Task<ExportExecutionResult> ExportAsync(ConnectedSystem system, ConnectedSystemExportResult exportResult)
    {
        var mockConnector = new Mock<IConnector>();
        var mockExportConnector = mockConnector.As<IConnectorExportUsingCalls>();
        mockConnector.Setup(c => c.Name).Returns("Test Connector");
        mockExportConnector.Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                exports.Select(_ => exportResult).ToList());

        return Jim.ExportExecution.ExecuteExportsAsync(system, mockConnector.Object, SyncRunMode.PreviewAndSync);
    }

    #endregion
}
