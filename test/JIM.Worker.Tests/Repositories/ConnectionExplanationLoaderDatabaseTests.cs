// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the loaders behind a Metaverse Object's connection explanations (#348). The
/// in-memory provider fixes navigations up whether or not a query loads them, so only a real database shows that each
/// loader returns what the explanation needs and nothing it does not. Run through a NoTracking context, as the portal
/// runs. Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectionExplanationLoaderDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.TrackAll)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    private PostgresDataRepository NewPortalRepository() => new(NewContext(QueryTrackingBehavior.NoTracking));

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL connection explanation loader tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private sealed class Seeded
    {
        public int PersonTypeId { get; init; }
        public int DepartmentAttributeId { get; init; }
        public int CostCentreAttributeId { get; init; }
        public int JobTitleAttributeId { get; init; }
        public int CsDepartmentAttributeId { get; init; }
        public int CsTitleAttributeId { get; init; }
        public int ExportRuleId { get; init; }
        public int ImportRuleId { get; init; }
        public Guid MvoId { get; init; }
        public Guid[] CsoIds { get; init; } = [];
    }

    /// <summary>
    /// Finance App (active) has an enabled export rule for People with a three-level scoping tree, and a disabled one;
    /// Payroll (disabled) has an enabled import rule for People; a rule for Groups is never wanted. One Metaverse
    /// Object, four Connected System Objects.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var financeApp = new ConnectedSystem { Name = "Finance App", ConnectorDefinition = connectorDefinition };
        var payroll = new ConnectedSystem { Name = "Payroll", ConnectorDefinition = connectorDefinition, Status = ConnectedSystemStatus.Disabled };
        var financeType = new ConnectedSystemObjectType { Name = "account", ConnectedSystem = financeApp, Selected = true };
        var payrollType = new ConnectedSystemObjectType { Name = "employee", ConnectedSystem = payroll, Selected = true };
        var csExternalId = new ConnectedSystemObjectTypeAttribute { Name = "id", ConnectedSystemObjectType = payrollType, Type = AttributeDataType.Text, Selected = true, IsExternalId = true };
        var csDepartment = new ConnectedSystemObjectTypeAttribute { Name = "dept", ConnectedSystemObjectType = payrollType, Type = AttributeDataType.Text, Selected = true };
        var csTitle = new ConnectedSystemObjectTypeAttribute { Name = "title", ConnectedSystemObjectType = payrollType, Type = AttributeDataType.Text, Selected = true };
        payrollType.Attributes.AddRange([csExternalId, csDepartment, csTitle]);
        var financeExternalId = new ConnectedSystemObjectTypeAttribute { Name = "id", ConnectedSystemObjectType = financeType, Type = AttributeDataType.Text, Selected = true, IsExternalId = true };
        financeType.Attributes.Add(financeExternalId);

        var person = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var group = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        var department = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var costCentre = new MetaverseAttribute { Name = "Cost Centre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var jobTitle = new MetaverseAttribute { Name = "Job Title", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };

        var exportRule = new SyncRule
        {
            Name = "Finance App Users Export", Direction = SyncRuleDirection.Export, Enabled = true, ProvisionToConnectedSystem = true,
            ConnectedSystem = financeApp, ConnectedSystemObjectType = financeType, MetaverseObjectType = person,
            ObjectScopingCriteriaGroups =
            [
                new SyncRuleScopingCriteriaGroup
                {
                    Type = SearchGroupType.All,
                    Criteria = [new SyncRuleScopingCriteria { MetaverseAttribute = department, ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" }],
                    ChildGroups =
                    [
                        new SyncRuleScopingCriteriaGroup
                        {
                            Type = SearchGroupType.Any,
                            Criteria = [new SyncRuleScopingCriteria { MetaverseAttribute = costCentre, ComparisonType = SearchComparisonType.StartsWith, StringValue = "FIN" }],
                            ChildGroups =
                            [
                                new SyncRuleScopingCriteriaGroup
                                {
                                    Type = SearchGroupType.All,
                                    Criteria = [new SyncRuleScopingCriteria { MetaverseAttribute = jobTitle, ComparisonType = SearchComparisonType.Contains, StringValue = "Accountant" }]
                                }
                            ]
                        }
                    ]
                }
            ]
        };
        var disabledExportRule = new SyncRule
        {
            Name = "Finance App Contractors Export", Direction = SyncRuleDirection.Export, Enabled = false,
            ConnectedSystem = financeApp, ConnectedSystemObjectType = financeType, MetaverseObjectType = person
        };
        var importRule = new SyncRule
        {
            Name = "Payroll Import", Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystem = payroll, ConnectedSystemObjectType = payrollType, MetaverseObjectType = person,
            ObjectScopingCriteriaGroups =
            [
                new SyncRuleScopingCriteriaGroup
                {
                    Type = SearchGroupType.All,
                    Criteria = [new SyncRuleScopingCriteria { ConnectedSystemAttribute = csDepartment, ComparisonType = SearchComparisonType.NotEquals, StringValue = "Test" }]
                }
            ]
        };
        var groupRule = new SyncRule
        {
            Name = "Finance App Groups Export", Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystem = financeApp, ConnectedSystemObjectType = financeType, MetaverseObjectType = group
        };

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = person, Created = DateTime.UtcNow };
        mvo.AttributeValues.AddRange(
        [
            new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = department, StringValue = "Finance" },
            new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = costCentre, StringValue = "FIN1" },
            new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = costCentre, StringValue = "FIN2" },
            new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = jobTitle, NullValue = true }
        ]);

        seed.AddRange(connectorDefinition, financeApp, payroll, financeType, payrollType, person, group, department, costCentre, jobTitle,
            exportRule, disabledExportRule, importRule, groupRule, mvo);
        await seed.SaveChangesAsync();

        var csos = Enumerable.Range(0, 4).Select(i => new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = payroll.Id,
            TypeId = payrollType.Id,
            ExternalIdAttributeId = csExternalId.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        }).ToArray();
        csos[0].AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = csDepartment.Id, StringValue = "Finance" });
        csos[0].AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = csTitle.Id, StringValue = "Accountant" });
        csos[1].AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = csDepartment.Id, StringValue = "Sales" });
        seed.ConnectedSystemObjects.AddRange(csos);
        await seed.SaveChangesAsync();

        return new Seeded
        {
            PersonTypeId = person.Id,
            DepartmentAttributeId = department.Id,
            CostCentreAttributeId = costCentre.Id,
            JobTitleAttributeId = jobTitle.Id,
            CsDepartmentAttributeId = csDepartment.Id,
            CsTitleAttributeId = csTitle.Id,
            ExportRuleId = exportRule.Id,
            ImportRuleId = importRule.Id,
            MvoId = mvo.Id,
            CsoIds = csos.Select(c => c.Id).ToArray()
        };
    }

    [Test]
    public async Task GetSyncRulesForScopingExplanationAsync_TypeWithRules_ReturnsEnabledRulesWithWhatExplanationsNeedAsync()
    {
        var s = await SeedAsync();

        using var repository = NewPortalRepository();
        var rules = await repository.ConnectedSystems.GetSyncRulesForScopingExplanationAsync(s.PersonTypeId);

        Assert.That(rules.Select(r => r.Name), Is.EqualTo(new[] { "Finance App Users Export", "Payroll Import" }),
            "enabled rules of the type only, both directions, in name order");
        var export = rules[0];
        var import = rules[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.Direction, Is.EqualTo(SyncRuleDirection.Export));
            Assert.That(export.ConnectedSystem.Name, Is.EqualTo("Finance App"));
            Assert.That(export.ConnectedSystem.Status, Is.EqualTo(ConnectedSystemStatus.Active));
            Assert.That(export.ConnectedSystemObjectType!.Name, Is.EqualTo("account"));
            Assert.That(export.ProvisionToConnectedSystem, Is.True);
            Assert.That(import.ConnectedSystem.Status, Is.EqualTo(ConnectedSystemStatus.Disabled));
        }

        // The whole tree, every level, with each criterion's attribute.
        var top = export.ObjectScopingCriteriaGroups.Single();
        var child = top.ChildGroups.Single();
        var grandchild = child.ChildGroups.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(top.Criteria.Single().MetaverseAttribute!.Name, Is.EqualTo("Department"));
            Assert.That(child.Type, Is.EqualTo(SearchGroupType.Any));
            Assert.That(child.Criteria.Single().MetaverseAttribute!.Name, Is.EqualTo("Cost Centre"));
            Assert.That(grandchild.Criteria.Single().MetaverseAttribute!.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(grandchild.Criteria.Single().StringValue, Is.EqualTo("Accountant"));
            Assert.That(import.ObjectScopingCriteriaGroups.Single().Criteria.Single().ConnectedSystemAttribute!.Name, Is.EqualTo("dept"));
        }
    }

    [Test]
    public async Task GetMetaverseObjectAttributeValuesAsync_RequestedAttributes_ReturnsEveryValueOfThoseOnlyAsync()
    {
        var s = await SeedAsync();

        using var repository = NewPortalRepository();
        var values = await repository.Metaverse.GetMetaverseObjectAttributeValuesAsync(s.MvoId, [s.CostCentreAttributeId, s.JobTitleAttributeId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values.Where(v => v.AttributeId == s.CostCentreAttributeId).Select(v => v.StringValue),
                Is.EquivalentTo(new[] { "FIN1", "FIN2" }), "multi-valued attributes are loaded uncapped");
            Assert.That(values.Single(v => v.AttributeId == s.JobTitleAttributeId).NullValue, Is.True,
                "an asserted-null marker must arrive marked, so scoping reads it as no value");
            Assert.That(values.Any(v => v.AttributeId == s.DepartmentAttributeId), Is.False);
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectAttributeValuesAsync_RequestedObjectsAndAttributes_ReturnsThoseOnlyAsync()
    {
        var s = await SeedAsync();

        using var repository = NewPortalRepository();
        var values = await repository.ConnectedSystems.GetConnectedSystemObjectAttributeValuesAsync(
            [s.CsoIds[0], s.CsoIds[2]], [s.CsDepartmentAttributeId]);

        var value = values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.ConnectedSystemObject.Id, Is.EqualTo(s.CsoIds[0]));
            Assert.That(value.AttributeId, Is.EqualTo(s.CsDepartmentAttributeId));
            Assert.That(value.StringValue, Is.EqualTo("Finance"));
        }
    }

    [Test]
    public async Task GetJoinHistoryAsync_RetainedHistory_ReturnsEachJoinWithItsActivityAndRuleAsync()
    {
        var s = await SeedAsync();
        var (projected, joined, provisioned, pending) = (s.CsoIds[0], s.CsoIds[1], s.CsoIds[2], s.CsoIds[3]);

        var syncActivity = new Activity
        {
            Id = Guid.NewGuid(), TargetName = "Payroll Full Synchronisation", TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete, InitiatedByType = ActivityInitiatorType.System,
            Created = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc), TotalActivityTime = TimeSpan.FromMinutes(20)
        };
        var exportActivity = new Activity
        {
            Id = Guid.NewGuid(), TargetName = "Finance App Export", TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete, InitiatedByType = ActivityInitiatorType.System
        };
        var projection = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = syncActivity.Id, ObjectChangeType = ObjectChangeType.Projected, ConnectedSystemObjectId = projected };
        projection.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(), OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected, SyncRuleId = s.ImportRuleId, SyncRuleName = "Payroll Import"
        });
        var attributeFlow = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = syncActivity.Id, ObjectChangeType = ObjectChangeType.AttributeFlow, ConnectedSystemObjectId = projected };
        var join = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = syncActivity.Id, ObjectChangeType = ObjectChangeType.Joined, ConnectedSystemObjectId = joined };
        var provisioningSync = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = syncActivity.Id, ObjectChangeType = ObjectChangeType.AttributeFlow };
        var export = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = exportActivity.Id, ObjectChangeType = ObjectChangeType.Exported, ConnectedSystemObjectId = provisioned };
        var queueing = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = syncActivity.Id, ObjectChangeType = ObjectChangeType.AttributeFlow };

        await using (var ctx = NewContext())
        {
            ctx.Activities.AddRange(syncActivity, exportActivity);
            ctx.ActivityRunProfileExecutionItems.AddRange(projection, attributeFlow, join, provisioningSync, export, queueing);
            ctx.CausalEdges.Add(new CausalEdge
            {
                Id = Guid.NewGuid(),
                EffectRunProfileExecutionItemId = export.Id,
                CauseRunProfileExecutionItemId = provisioningSync.Id,
                EdgeType = CausalEdgeType.PendingExportQueueingCausedExportExecution,
                ReasonCode = CausalReasonCode.ExportCreateStaged,
                SyncRuleId = s.ExportRuleId,
                SyncRuleName = "Finance App Users Export"
            });
            await ctx.SaveChangesAsync();
        }

        using var repository = NewPortalRepository();
        var history = await repository.ConnectedSystems.GetJoinHistoryAsync(
            [projected, joined, provisioned, pending], new Dictionary<Guid, Guid> { [pending] = queueing.Id });

        Assert.That(history, Has.Count.EqualTo(4), "the Attribute Flow item is not join history");
        var byCso = history.ToDictionary(h => h.ConnectedSystemObjectId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(byCso[projected].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Projected));
            Assert.That(byCso[projected].RunProfileExecutionItemId, Is.EqualTo(projection.Id));
            Assert.That(byCso[projected].ActivityId, Is.EqualTo(syncActivity.Id));
            Assert.That(byCso[projected].ActivityCreated, Is.EqualTo(syncActivity.Created));
            Assert.That(byCso[projected].ActivityDuration, Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(byCso[projected].SyncRuleName, Is.EqualTo("Payroll Import"));

            Assert.That(byCso[joined].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
            Assert.That(byCso[joined].SyncRuleName, Is.Null, "an inbound join's history names no rule");

            Assert.That(byCso[provisioned].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned));
            Assert.That(byCso[provisioned].ActivityId, Is.EqualTo(syncActivity.Id), "the synchronisation that provisioned it, not the export");
            Assert.That(byCso[provisioned].RunProfileExecutionItemId, Is.EqualTo(provisioningSync.Id));
            Assert.That(byCso[provisioned].SyncRuleId, Is.EqualTo(s.ExportRuleId));

            Assert.That(byCso[pending].JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned));
            Assert.That(byCso[pending].ActivityId, Is.EqualTo(syncActivity.Id));
            Assert.That(byCso[pending].RunProfileExecutionItemId, Is.EqualTo(queueing.Id));
        }
    }
}
