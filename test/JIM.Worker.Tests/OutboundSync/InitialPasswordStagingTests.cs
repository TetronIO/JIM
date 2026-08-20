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
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Staging of initial passwords during export execution (#1121).
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

        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    /// <summary>
    /// A Create exported by a Synchronisation Rule that asks for an initial password leaves a record that the
    /// account is owed one, carrying enough to attempt delivery later: the account, its Connected System, and
    /// the rule whose settings govern the password.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_CreateFromARuleThatAsksForAPassword_StagesTheAccountAsync()
    {
        var (system, cso, _) = ArrangeProvisioningCreate(initialPasswordEnabled: true);

        var result = await ExportAsync(system, ConnectedSystemExportResult.Succeeded());

        Assert.That(result.SuccessCount, Is.EqualTo(1));
        Assert.That(SyncRepo.PendingInitialPasswords, Has.Count.EqualTo(1));

        var staged = SyncRepo.PendingInitialPasswords.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(staged.ConnectedSystemId, Is.EqualTo(system.Id));
            Assert.That(staged.SyncRuleId, Is.EqualTo(ProvisioningRuleId));
            Assert.That(staged.Status, Is.EqualTo(PendingInitialPasswordStatus.Pending));
            Assert.That(staged.AttemptCount, Is.Zero, "nothing has been attempted at staging time");
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1));
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

        var staged = SyncRepo.PendingInitialPasswords.Values.Single();
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

        var staged = SyncRepo.PendingInitialPasswords.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(system.InitialPasswordTimeToLive, Is.Null);
            Assert.That(staged.ExpiresAt, Is.InRange(
                before.Add(PendingInitialPassword.DefaultTimeToLive),
                after.Add(PendingInitialPassword.DefaultTimeToLive)));
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
        Assert.That(SyncRepo.PendingInitialPasswords, Is.Empty);
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
        Assert.That(SyncRepo.PendingInitialPasswords, Is.Empty);
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
        Assert.That(SyncRepo.PendingInitialPasswords, Is.Empty);
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
        SyncRepo.FailInitialPasswordStagingWith = new InvalidOperationException("The database rejected the work list row.");

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
        Assert.That(SyncRepo.PendingInitialPasswords, Is.Empty);
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
        Assert.That(SyncRepo.PendingInitialPasswords, Has.Count.EqualTo(1));

        var staged = SyncRepo.PendingInitialPasswords.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.ConnectedSystemObjectId, Is.EqualTo(passwordCso.Id));
            Assert.That(staged.ConnectedSystemObjectId, Is.Not.EqualTo(otherCso.Id));
            Assert.That(result.InitialPasswordsStagedCount, Is.EqualTo(1));
        }
    }

    #region Helper Methods

    private const int ProvisioningRuleId = 91121;
    private const int OtherProvisioningRuleId = 91122;

    /// <summary>
    /// Sets up one Create Pending Export, provisioned by a Synchronisation Rule that either does or does not
    /// ask for an initial password.
    /// </summary>
    private (ConnectedSystem System, ConnectedSystemObject Cso, PendingExport Export) ArrangeProvisioningCreate(
        bool initialPasswordEnabled,
        int syncRuleId = ProvisioningRuleId)
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
            AttributeValues = []
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
