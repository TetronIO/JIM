// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL synchronisation runs that stop part-way through one of the passes that follow the page loop (#3085):
/// cross-page reference resolution and the export scope review. Each pass writes a batch in several steps, each committed
/// on its own, so a run that stops between two of them leaves part of the batch behind. These runs show what it leaves,
/// and whether the next run puts it right.
/// </summary>
/// <remarks>
/// The in-memory repository has no transactions, so only PostgreSQL can show any of this. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PostPagePassAtomicityDatabaseTests
{
    private static readonly Guid Alpha = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Beta = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private string _connectionString = null!;
    private int _sourceId;
    private int _sourceTypeId;
    private int _sourceExternalIdAttributeId;
    private int _sourceDisplayNameAttributeId;
    private int _sourceMemberAttributeId;
    private int _directoryId;
    private int _directoryTypeId;
    private int _directoryNameAttributeId;
    private int _directoryMemberAttributeId;
    private int _mvGroupTypeId;
    private int _mvDisplayNameAttributeId;
    private int _mvMemberAttributeId;

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); a synchronisation run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL post-page pass atomicity tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await SeedConfigurationAsync();
    }

    /// <summary>
    /// Alpha contains Beta, which sits on a later page, so Alpha's membership resolves only in the cross-page reference
    /// pass. The pass writes Alpha's membership to its Metaverse Object, then re-stages Alpha's provisioning with the
    /// member; a run that stops between those writes must not leave the Directory group without its member, or without
    /// any export at all, once the next run has finished.
    /// </summary>
    [Test]
    public async Task FullSync_StoppedAtTheCrossPagePassesPendingExportWrite_NextRunStagesTheGroupWithItsMemberAsync()
    {
        await AddDirectoryExportRuleAsync();
        await CreateGroupsAsync(withMembership: true);

        Assert.That(async () => await RunAsync(ConnectedSystemRunType.FullSynchronisation,
                wrap: (repository, context) => FailBefore(repository, context, nameof(ISyncRepository.CreatePendingExportsAsync),
                    when: args => StagesAMember((IEnumerable<PendingExport>)args[0]!))),
            Throws.Exception, "precondition: the run stopped in the cross-page pass, staging Alpha's membership");
        await TestContext.Out.WriteLineAsync($"After the stop: {string.Join("; ", await DirectoryExportsAsync())}");

        await RunAsync(ConnectedSystemRunType.FullSynchronisation);

        Assert.That(await DirectoryExportsAsync(), Is.EquivalentTo(new[]
        {
            new DirectoryExport("Alpha", PendingExportChangeType.Create, Members: 1),
            new DirectoryExport("Beta", PendingExportChangeType.Create, Members: 0)
        }), "each group is provisioned once, and Alpha's Create carries its member");
    }

    /// <summary>
    /// As above, then the next run is a Delta Synchronisation, which the scheduler runs far more often than a Full one.
    /// </summary>
    [Test]
    public async Task DeltaSync_AfterARunStoppedAtTheCrossPagePassesPendingExportWrite_StagesTheGroupWithItsMemberAsync()
    {
        await AddDirectoryExportRuleAsync();
        await CreateGroupsAsync(withMembership: true);

        Assert.That(async () => await RunAsync(ConnectedSystemRunType.FullSynchronisation,
                wrap: (repository, context) => FailBefore(repository, context, nameof(ISyncRepository.CreatePendingExportsAsync),
                    when: args => StagesAMember((IEnumerable<PendingExport>)args[0]!))),
            Throws.Exception, "precondition: the run stopped in the cross-page pass, staging Alpha's membership");

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await DirectoryExportsAsync(), Is.EquivalentTo(new[]
        {
            new DirectoryExport("Alpha", PendingExportChangeType.Create, Members: 1),
            new DirectoryExport("Beta", PendingExportChangeType.Create, Members: 0)
        }), "each group is provisioned once, and Alpha's Create carries its member");
    }

    /// <summary>
    /// An export Synchronisation Rule that provisions is added after the groups were synchronised, so the scope review
    /// provisions them. It writes the Directory objects, then their Pending Exports, then clears the review flag; a run that
    /// stops between the first two keeps the flag, and the next run's review must still stage each object's Create.
    /// </summary>
    [Test]
    public async Task DeltaSync_StoppedAtTheScopeReviewsPendingExportWrite_NextRunStagesEachProvisionedObjectsCreateAsync()
    {
        await CreateGroupsAsync(withMembership: false);
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        await AddDirectoryExportRuleAsync();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.FlagMetaverseObjectsOfTypeForScopeReviewAsync(_mvGroupTypeId);

        Assert.That(async () => await RunAsync(ConnectedSystemRunType.DeltaSynchronisation,
                wrap: (repository, context) => FailBefore(repository, context, nameof(ISyncRepository.CreatePendingExportsAsync))),
            Throws.Exception, "precondition: the run stopped in the scope review, staging the provisioning");
        await TestContext.Out.WriteLineAsync($"After the stop: {string.Join("; ", await DirectoryExportsAsync())}");

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await DirectoryExportsAsync(), Is.EquivalentTo(new[]
        {
            new DirectoryExport("Alpha", PendingExportChangeType.Create, Members: 0),
            new DirectoryExport("Beta", PendingExportChangeType.Create, Members: 0)
        }), "each group is provisioned once, with its Create");
    }

    /// <summary>The control for the cross-page runs above: uninterrupted, the run provisions both groups with Alpha's member.</summary>
    [Test]
    public async Task FullSync_Uninterrupted_StagesEachGroupWithItsMembersAsync()
    {
        await AddDirectoryExportRuleAsync();
        await CreateGroupsAsync(withMembership: true);

        await RunAsync(ConnectedSystemRunType.FullSynchronisation);

        Assert.That(await DirectoryExportsAsync(), Is.EquivalentTo(new[]
        {
            new DirectoryExport("Alpha", PendingExportChangeType.Create, Members: 1),
            new DirectoryExport("Beta", PendingExportChangeType.Create, Members: 0)
        }));
    }

    /// <summary>The control for the scope review run above: uninterrupted, the review provisions both groups.</summary>
    [Test]
    public async Task DeltaSync_ScopeReviewUninterrupted_StagesEachProvisionedObjectsCreateAsync()
    {
        await CreateGroupsAsync(withMembership: false);
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        await AddDirectoryExportRuleAsync();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.FlagMetaverseObjectsOfTypeForScopeReviewAsync(_mvGroupTypeId);

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await DirectoryExportsAsync(), Is.EquivalentTo(new[]
        {
            new DirectoryExport("Alpha", PendingExportChangeType.Create, Members: 0),
            new DirectoryExport("Beta", PendingExportChangeType.Create, Members: 0)
        }));
    }

    /// <summary>
    /// A corrected generated value is carried to the queued exports by the revision drain, which replaces the object's
    /// queued Pending Export and deletes the revision record once the batch is written. A run that stops between removing
    /// the queued export and writing its replacement keeps the record, and the next run's drain must still leave the
    /// object's whole export queued, not just the revised value or nothing.
    /// </summary>
    [Test]
    public async Task DeltaSync_StoppedAtTheRevisionDrainsPendingExportWrite_NextRunKeepsTheObjectsWholeExportAsync()
    {
        await AddDirectoryExportRuleAsync();
        await CreateGroupsAsync(withMembership: true);
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        await RecordRevisionAsync(Alpha, _mvDisplayNameAttributeId);

        Assert.That(async () => await RunAsync(ConnectedSystemRunType.DeltaSynchronisation,
                wrap: (repository, context) => FailBefore(repository, context, nameof(ISyncRepository.CreatePendingExportsAsync))),
            Throws.Exception, "precondition: the run stopped in the revision drain, replacing Alpha's export");
        await TestContext.Out.WriteLineAsync($"After the stop: {string.Join("; ", await DirectoryExportsAsync())}");

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await DirectoryExportsAsync(), Is.EquivalentTo(new[]
        {
            new DirectoryExport("Alpha", PendingExportChangeType.Create, Members: 1),
            new DirectoryExport("Beta", PendingExportChangeType.Create, Members: 0)
        }), "Alpha's Create still carries its name and its member");
    }

    /// <summary>The control for the revision drain run above.</summary>
    [Test]
    public async Task DeltaSync_RevisionDrainUninterrupted_KeepsTheObjectsWholeExportAsync()
    {
        await AddDirectoryExportRuleAsync();
        await CreateGroupsAsync(withMembership: true);
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        await RecordRevisionAsync(Alpha, _mvDisplayNameAttributeId);

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await DirectoryExportsAsync(), Is.EquivalentTo(new[]
        {
            new DirectoryExport("Alpha", PendingExportChangeType.Create, Members: 1),
            new DirectoryExport("Beta", PendingExportChangeType.Create, Members: 0)
        }));
    }

    #region Harness

    /// <summary>A revision record for a Source group's Metaverse Object, as an export run that corrected the value leaves.</summary>
    private async Task RecordRevisionAsync(Guid sourceObjectId, int metaverseAttributeId)
    {
        await using var ctx = NewContext();
        var mvoId = await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == sourceObjectId).Select(c => c.MetaverseObjectId).SingleAsync();
        ctx.GeneratedValueRevisionsPending.Add(new GeneratedValueRevisionPending
        {
            Id = Guid.NewGuid(), MetaverseObjectId = mvoId!.Value, MetaverseAttributeId = metaverseAttributeId,
            ReasonCode = CausalReasonCode.GeneratedValueAlreadyInUse, RejectedByConnectedSystemId = _directoryId, RejectedByConnectedSystemName = "Directory"
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// One Directory object and its Pending Export: the cn its export carries (or its own, where it has none), the export's
    /// change type (null when it has none) and how many member values the export carries.
    /// </summary>
    private sealed record DirectoryExport(string? Name, PendingExportChangeType? ChangeType, int Members);

    private async Task<List<DirectoryExport>> DirectoryExportsAsync()
    {
        await using var ctx = NewContext();
        var objects = await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.ConnectedSystemId == _directoryId).Select(c => c.Id).ToListAsync();
        var exports = await ctx.PendingExports.AsNoTracking().Include(pe => pe.AttributeValueChanges)
            .Where(pe => pe.ConnectedSystemId == _directoryId).ToListAsync();
        return objects.Select(id =>
        {
            var objectExports = exports.Where(pe => pe.ConnectedSystemObjectId == id).ToList();
            if (objectExports.Count == 0)
                return new DirectoryExport(null, null, 0);
            if (objectExports.Count > 1)
                return new DirectoryExport($"{objectExports.Count} exports", null, 0);
            var export = objectExports[0];
            return new DirectoryExport(
                export.AttributeValueChanges.FirstOrDefault(c => c.AttributeId == _directoryNameAttributeId)?.StringValue,
                export.ChangeType,
                export.AttributeValueChanges.Count(c => c.AttributeId == _directoryMemberAttributeId));
        }).ToList();
    }

    private bool StagesAMember(IEnumerable<PendingExport> exports) =>
        exports.Any(pe => pe.AttributeValueChanges.Any(c => c.AttributeId == _directoryMemberAttributeId));

    /// <summary>
    /// Wraps <paramref name="repository"/> so <paramref name="methodName"/> fails instead of running, the first time
    /// <paramref name="when"/> accepts its arguments. It fails as a database write does, with a statement PostgreSQL
    /// refuses on the run's own connection, as <c>PageFlushAtomicityDatabaseTests</c> does.
    /// </summary>
    private static ISyncRepository FailBefore(ISyncRepository repository, JimDbContext runContext, string methodName,
        Func<object?[], bool>? when = null)
    {
        var failed = false;
        return InterceptingSyncRepositoryProxy.Create(repository, methodName, args =>
        {
            if (failed || (when != null && !when(args)))
                return;
            failed = true;
            runContext.Database.ExecuteSqlRaw("SELECT 1 / 0");
        });
    }

    /// <summary>
    /// A Source system of groups, which project and flow their display name and members, and a Directory system the
    /// groups are provisioned to by <see cref="AddDirectoryExportRuleAsync"/>. Page size 1, so Alpha and Beta sit on pages
    /// of their own; change tracking off.
    /// </summary>
    private async Task SeedConfigurationAsync()
    {
        await using var seed = NewContext();

        foreach (var (key, valueType, value) in new[]
                 {
                     (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, "1"),
                     ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "false"),
                     ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "false")
                 })
        {
            seed.ServiceSettingItems.Add(new ServiceSetting
            {
                Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation,
                ValueType = valueType, DefaultValue = value, Value = value
            });
        }

        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };

        (ConnectedSystem System, ConnectedSystemObjectType Type) GroupSystem(string name, string externalIdName, string nameAttributeName)
        {
            var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
            var type = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
            type.Attributes.Add(Attribute(type, externalIdName, AttributeDataType.Text, isExternalId: true));
            type.Attributes.Add(Attribute(type, nameAttributeName, AttributeDataType.Text));
            type.Attributes.Add(Attribute(type, "member", AttributeDataType.Reference, AttributePlurality.MultiValued));
            return (system, type);
        }

        var (source, sourceType) = GroupSystem("Source", "id", "displayName");
        var (directory, directoryType) = GroupSystem("Directory", "groupId", "cn");

        var mvGroup = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        var mvDisplayName = new MetaverseAttribute
        {
            Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [mvGroup]
        };
        var mvMember = new MetaverseAttribute
        {
            Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, MetaverseObjectTypes = [mvGroup]
        };

        seed.AddRange(connectorDefinition, source, directory, sourceType, directoryType, mvGroup, mvDisplayName, mvMember);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = source.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = source.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });

        var import = new SyncRule
        {
            Name = "Source Import", ConnectedSystem = source, Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectType = sourceType, MetaverseObjectType = mvGroup, ProjectToMetaverse = true
        };
        import.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = import, TargetMetaverseAttribute = mvDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = sourceType.Attributes.Single(a => a.Name == "displayName") } }
        });
        import.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = import, TargetMetaverseAttribute = mvMember,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = sourceType.Attributes.Single(a => a.Name == "member") } }
        });
        seed.SyncRules.Add(import);
        await seed.SaveChangesAsync();

        _sourceId = source.Id;
        _sourceTypeId = sourceType.Id;
        _sourceExternalIdAttributeId = sourceType.Attributes.Single(a => a.Name == "id").Id;
        _sourceDisplayNameAttributeId = sourceType.Attributes.Single(a => a.Name == "displayName").Id;
        _sourceMemberAttributeId = sourceType.Attributes.Single(a => a.Name == "member").Id;
        _directoryId = directory.Id;
        _directoryTypeId = directoryType.Id;
        _directoryNameAttributeId = directoryType.Attributes.Single(a => a.Name == "cn").Id;
        _directoryMemberAttributeId = directoryType.Attributes.Single(a => a.Name == "member").Id;
        _mvGroupTypeId = mvGroup.Id;
        _mvDisplayNameAttributeId = mvDisplayName.Id;
        _mvMemberAttributeId = mvMember.Id;
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType,
        AttributePlurality plurality = AttributePlurality.SingleValued, bool isExternalId = false) => new()
    {
        Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true, IsExternalId = isExternalId
    };

    /// <summary>The Directory's export rule for groups: provisions every group, flowing its display name and members.</summary>
    private async Task AddDirectoryExportRuleAsync()
    {
        await using var seed = NewContext();
        var rule = new SyncRule
        {
            Name = "Directory Export", Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystemId = _directoryId, ConnectedSystemObjectTypeId = _directoryTypeId, MetaverseObjectTypeId = _mvGroupTypeId,
            ProvisionToConnectedSystem = true, OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect,
            // Added before or after the objects it covers were synchronised, as a configuration change is.
            Created = DateTime.UtcNow.AddMinutes(-30)
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule, TargetConnectedSystemAttributeId = _directoryNameAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttributeId = _mvDisplayNameAttributeId } }
        });
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule, TargetConnectedSystemAttributeId = _directoryMemberAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttributeId = _mvMemberAttributeId } }
        });
        seed.SyncRules.Add(rule);
        await seed.SaveChangesAsync();
    }

    /// <summary>Alpha and Beta, created two hours ago through the import's own create writer; Alpha contains Beta when asked.</summary>
    private async Task CreateGroupsAsync(bool withMembership)
    {
        await using var ctx = NewContext();
        var created = DateTime.UtcNow.AddHours(-2);

        ConnectedSystemObject Group(Guid id, string name)
        {
            var cso = new ConnectedSystemObject
            {
                Id = id, ConnectedSystemId = _sourceId, TypeId = _sourceTypeId, ExternalIdAttributeId = _sourceExternalIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal, Created = created
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _sourceExternalIdAttributeId, StringValue = name });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _sourceDisplayNameAttributeId, StringValue = name });
            return cso;
        }

        var alpha = Group(Alpha, "Alpha");
        if (withMembership)
            alpha.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _sourceMemberAttributeId, ReferenceValueId = Beta });

        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([alpha, Group(Beta, "Beta")]);
    }

    /// <summary>
    /// Runs one synchronisation of the Source system on its own tracking context, as the Worker does. <paramref name="wrap"/>
    /// wraps the run's repository and is handed the run's context, so a test can make a write fail on the run's own
    /// connection.
    /// </summary>
    private async Task RunAsync(ConnectedSystemRunType runType, Func<ISyncRepository, JimDbContext, ISyncRepository>? wrap = null)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        var syncRepository = wrap?.Invoke(repository.Sync, ctx) ?? repository.Sync;
        using var jim = new JimApplication(repository, syncRepository: syncRepository);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_sourceId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        if (runType == ConnectedSystemRunType.FullSynchronisation)
            await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), syncRepository, system, runProfile, activity, new CancellationTokenSource())
                .PerformFullSyncAsync();
        else
            await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), syncRepository, system, runProfile, activity, new CancellationTokenSource())
                .PerformDeltaSyncAsync();

        await using var read = NewContext();
        var errors = await read.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activity.Id && i.ErrorType != null && i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToListAsync();
        Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
    }

    #endregion
}
