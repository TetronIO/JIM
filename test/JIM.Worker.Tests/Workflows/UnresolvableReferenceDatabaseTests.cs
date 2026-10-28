// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL runs over a group that references an object no Synchronisation Rule joins (a contact). Whether such a
/// reference is final for the run is decided from the referenced object's type and creation time, which the sync page
/// loader reads by direct SQL; the in-memory suite (<see cref="UnresolvableReferenceWorkflowTests"/>) reads them from
/// navigations instead, so only a real database proves the loader supplies them.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class UnresolvableReferenceDatabaseTests
{
    private string _connectionString = null!;
    private int _systemId;
    private readonly Dictionary<string, int> _typeIds = [];
    private readonly Dictionary<string, int> _externalIdAttributeIds = [];
    private readonly Dictionary<string, int> _displayNameAttributeIds = [];
    private int _memberAttributeId;
    private int _mvDisplayNameAttributeId;
    private int _mvMemberAttributeId;

    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Contact = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Team = Guid.Parse("00000000-0000-0000-0000-000000000004");

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
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL unresolvable reference tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _typeIds.Clear();
        _externalIdAttributeIds.Clear();
        _displayNameAttributeIds.Clear();
        await SeedConfigurationAsync();
        await CreateObjectsAsync();
    }

    [Test]
    public async Task FullSync_RepeatedOverAGroupReferencingAContact_FlowsTheGroupInPageAndRemovesARemovedMemberAsync()
    {
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        Assert.That(await MemberDisplayNamesAsync(), Is.EquivalentTo(new[] { "Alice", "Bob" }), "precondition: both people flowed; the contact cannot");

        await RemoveMemberAsync(Bob);
        var processor = await RunAsync(ConnectedSystemRunType.FullSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await MemberDisplayNamesAsync(), Is.EquivalentTo(new[] { "Alice" }),
                "the removed member leaves the Metaverse although the group still references the contact");
            Assert.That(processor.CrossPageReferenceObjectCount, Is.Zero,
                "the contact's type and age, read by the page loader, mark its reference final, so the group is not deferred");
            Assert.That(processor.UnresolvableReferenceValueCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeltaSync_MemberRemovedFromAGroupReferencingAContact_RemovesTheMemberAsync()
    {
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);

        await RemoveMemberAsync(Bob);
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await MemberDisplayNamesAsync(), Is.EquivalentTo(new[] { "Alice" }));
    }

    #region Harness

    /// <summary>
    /// One Connected System with User, Contact and Group types. People and groups project, and the group's Member flows;
    /// no Synchronisation Rule covers contacts. Page size 2, so the run crosses pages; change tracking off.
    /// </summary>
    private async Task SeedConfigurationAsync()
    {
        await using var seed = NewContext();

        foreach (var (key, valueType, value) in new[]
                 {
                     (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, "2"),
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
        var system = new ConnectedSystem
        {
            Name = "Directory", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };

        var types = new Dictionary<string, ConnectedSystemObjectType>();
        foreach (var name in new[] { "user", "contact", "group" })
        {
            var type = new ConnectedSystemObjectType { Name = name, ConnectedSystem = system, Selected = true };
            type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Name = "id", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            });
            type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Name = "displayName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true
            });
            types[name] = type;
        }

        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = types["group"], Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        types["group"].Attributes.Add(member);

        var mvPerson = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvGroup = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        var mvDisplayName = new MetaverseAttribute
        {
            Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvPerson, mvGroup]
        };
        var mvMember = new MetaverseAttribute
        {
            Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
            MetaverseObjectTypes = [mvGroup]
        };

        seed.AddRange(connectorDefinition, system, mvPerson, mvGroup, mvDisplayName, mvMember);
        seed.AddRange(types.Values);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });
        await seed.SaveChangesAsync();

        SyncRule ImportRule(string name, ConnectedSystemObjectType type, MetaverseObjectType mvType)
        {
            var rule = new SyncRule
            {
                Name = name, ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
                ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = true
            };
            rule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = rule, TargetMetaverseAttribute = mvDisplayName,
                Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = type.Attributes.Single(a => a.Name == "displayName") } }
            });
            return rule;
        }

        var groupRule = ImportRule("Group Import", types["group"], mvGroup);
        groupRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = groupRule, TargetMetaverseAttribute = mvMember,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = member } }
        });
        seed.SyncRules.AddRange(ImportRule("User Import", types["user"], mvPerson), groupRule);
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        foreach (var (name, type) in types)
        {
            _typeIds[name] = type.Id;
            _externalIdAttributeIds[name] = type.Attributes.Single(a => a.Name == "id").Id;
            _displayNameAttributeIds[name] = type.Attributes.Single(a => a.Name == "displayName").Id;
        }
        _memberAttributeId = member.Id;
        _mvDisplayNameAttributeId = mvDisplayName.Id;
        _mvMemberAttributeId = mvMember.Id;
    }

    /// <summary>
    /// Alice and Bob (users), a contact, and Team, whose members are all three; created two hours ago through the
    /// import's own create writer.
    /// </summary>
    private async Task CreateObjectsAsync()
    {
        await using var ctx = NewContext();
        var created = DateTime.UtcNow.AddHours(-2);

        ConnectedSystemObject Cso(Guid id, string type, string displayName)
        {
            var cso = new ConnectedSystemObject
            {
                Id = id, ConnectedSystemId = _systemId, TypeId = _typeIds[type], ExternalIdAttributeId = _externalIdAttributeIds[type],
                Status = ConnectedSystemObjectStatus.Normal, Created = created
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _externalIdAttributeIds[type], StringValue = displayName });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeIds[type], StringValue = displayName });
            return cso;
        }

        var team = Cso(Team, "group", "Team");
        foreach (var memberId in new[] { Alice, Bob, Contact })
            team.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _memberAttributeId, ReferenceValueId = memberId });

        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(
            [Cso(Alice, "user", "Alice"), Cso(Bob, "user", "Bob"), Cso(Contact, "contact", "Contact"), team]);
    }

    /// <summary>An import removing a member from Team on its own connection, through the import's own update writer.</summary>
    private async Task RemoveMemberAsync(Guid memberId)
    {
        await using var ctx = NewContext();
        var team = await ctx.ConnectedSystemObjects.AsNoTracking().Include(c => c.AttributeValues).SingleAsync(c => c.Id == Team);
        var removed = team.AttributeValues.Single(av => av.AttributeId == _memberAttributeId && av.ReferenceValueId == memberId);
        team.LastUpdated = DateTime.UtcNow;
        await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsAsync([team], [], [removed.Id]);
    }

    /// <summary>The display names of the members on Team's Metaverse Object.</summary>
    private async Task<List<string?>> MemberDisplayNamesAsync()
    {
        await using var ctx = NewContext();
        var teamMvoId = await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == Team).Select(c => c.MetaverseObjectId).SingleAsync();
        var memberMvoIds = await ctx.MetaverseObjectAttributeValues.AsNoTracking()
            .Where(av => av.MetaverseObject.Id == teamMvoId && av.AttributeId == _mvMemberAttributeId)
            .Select(av => av.ReferenceValueId)
            .ToListAsync();
        return await ctx.MetaverseObjectAttributeValues.AsNoTracking()
            .Where(av => memberMvoIds.Contains(av.MetaverseObject.Id) && av.AttributeId == _mvDisplayNameAttributeId)
            .Select(av => av.StringValue)
            .ToListAsync();
    }

    /// <summary>Runs one synchronisation on its own tracking context, as the Worker does.</summary>
    private async Task<SyncTaskProcessorBase> RunAsync(ConnectedSystemRunType runType)
    {
        await using var ctx = NewContext(trackAll: true);
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        SyncTaskProcessorBase processor;
        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            var full = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                system, runProfile, activity, new CancellationTokenSource());
            await full.PerformFullSyncAsync();
            processor = full;
        }
        else
        {
            var delta = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                system, runProfile, activity, new CancellationTokenSource());
            await delta.PerformDeltaSyncAsync();
            processor = delta;
        }

        var errors = activity.RunProfileExecutionItems.Where(i => i.ErrorType is { } e && e != ActivityRunProfileExecutionItemErrorType.NotSet).ToList();
        Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
        return processor;
    }

    #endregion
}
