// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Two ways configuration could be left reading attributes that are not there (#2265): deselecting a Connected System
/// attribute a Scoping Criterion reads (it stops being imported, so the criterion compares nothing and takes objects
/// out of scope), and moving a saved Synchronisation Rule to another Object Type. Both are refused. An Attribute Flow
/// Expression reading a deselected attribute is only warned about. Real PostgreSQL, because the stored states the
/// refusals judge against are read untracked from the database.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AttributeDeselectionReaderRefusalDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL attribute deselection tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task UpdateAttributeAsync_DeselectingAnAttributeAScopingCriterionReads_RefusesNamingItAsync()
    {
        var seeded = await SeedAsync(scopeOnDepartment: true, flowReadsDepartment: false);

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var department = (await jim.ConnectedSystems.GetAttributeAsync(seeded.DepartmentId))!;
            department.Selected = false;

            var refusal = Assert.ThrowsAsync<AttributeReadByConfigurationException>(() =>
                jim.ConnectedSystems.UpdateAttributeAsync(department, TestUtilities.GetInitiatedBy()));
            Assert.That(refusal!.Message, Does.Contain("HR Users Inbound"));
        }

        await using var verify = NewContext();
        Assert.That((await verify.ConnectedSystemAttributes.SingleAsync(a => a.Id == seeded.DepartmentId)).Selected, Is.True);
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_DeselectingAnAttributeAScopingCriterionReads_RefusesAsync()
    {
        var seeded = await SeedAsync(scopeOnDepartment: true, flowReadsDepartment: false);

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var connectedSystem = (await jim.ConnectedSystems.GetConnectedSystemAsync(seeded.ConnectedSystemId))!;
            connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Id == seeded.DepartmentId).Selected = false;

            Assert.ThrowsAsync<AttributeReadByConfigurationException>(() =>
                jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, TestUtilities.GetInitiatedBy()));
        }

        await using var verify = NewContext();
        Assert.That((await verify.ConnectedSystemAttributes.SingleAsync(a => a.Id == seeded.DepartmentId)).Selected, Is.True);
    }

    [Test]
    public async Task UpdateAttributeAsync_DeselectingAnAttributeOnlyAnAttributeFlowExpressionReads_SavesWithAWarningAsync()
    {
        var seeded = await SeedAsync(scopeOnDepartment: false, flowReadsDepartment: true);

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var department = (await jim.ConnectedSystems.GetAttributeAsync(seeded.DepartmentId))!;
            department.Selected = false;
            await jim.ConnectedSystems.UpdateAttributeAsync(department, TestUtilities.GetInitiatedBy());
        }

        await using var verify = NewContext();
        var activity = await verify.Activities.SingleAsync(a => a.TargetOperationType == ActivityTargetOperationType.Update);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await verify.ConnectedSystemAttributes.SingleAsync(a => a.Id == seeded.DepartmentId)).Selected, Is.False);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.WarningMessage, Does.Contain("HR Users Inbound"));
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_MetaverseObjectTypeChangedOnASavedRule_RefusesAsync()
    {
        var seeded = await SeedAsync(scopeOnDepartment: false, flowReadsDepartment: false);

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(seeded.SyncRuleId))!;
            var groupType = (await jim.Metaverse.GetMetaverseObjectTypeAsync(seeded.GroupTypeId, false))!;
            rule.MetaverseObjectType = groupType;
            rule.MetaverseObjectTypeId = groupType.Id;

            var refusal = Assert.ThrowsAsync<ArgumentException>(() => jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, TestUtilities.GetInitiatedBy()));
            Assert.That(refusal!.Message, Does.Contain("Metaverse Object Type").And.Contain("cannot be changed"));
        }

        await using var verify = NewContext();
        Assert.That((await verify.SyncRules.SingleAsync(r => r.Id == seeded.SyncRuleId)).MetaverseObjectTypeId, Is.EqualTo(seeded.PersonTypeId));
    }

    private sealed record Seeded(int ConnectedSystemId, int DepartmentId, int SyncRuleId, int PersonTypeId, int GroupTypeId);

    private async Task<Seeded> SeedAsync(bool scopeOnDepartment, bool flowReadsDepartment)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
        var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var employeeId = new ConnectedSystemObjectTypeAttribute { Name = "employeeId", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = userType, Selected = true, IsExternalId = true };
        var department = new ConnectedSystemObjectTypeAttribute { Name = "department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = userType, Selected = true };
        userType.Attributes.Add(employeeId);
        userType.Attributes.Add(department);
        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var description = new MetaverseAttribute { Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        personType.Attributes.Add(description);

        var rule = new SyncRule
        {
            Name = "HR Users Inbound",
            Direction = SyncRuleDirection.Import,
            Enabled = false,
            ConnectedSystem = system,
            ConnectedSystemObjectType = userType,
            MetaverseObjectType = personType
        };
        if (scopeOnDepartment)
            rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = [new SyncRuleScopingCriteria { ConnectedSystemAttribute = department, ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" }]
            });
        if (flowReadsDepartment)
        {
            var mapping = new SyncRuleMapping { TargetMetaverseAttribute = description };
            mapping.Sources.Add(new SyncRuleMappingSource { Expression = "\"Dept: \" + cs[\"department\"]", Order = 0 });
            rule.AttributeFlowRules.Add(mapping);
        }

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.MetaverseObjectTypes.AddRange(personType, groupType);
        seed.SyncRules.Add(rule);
        await seed.SaveChangesAsync();
        return new Seeded(system.Id, department.Id, rule.Id, personType.Id, groupType.Id);
    }
}
