// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Apply and Remove deletes the rows of attributes a schema refresh no longer reports. Until #2265 it did so with
/// Scoping Criteria still comparing them, which left a Synchronisation Rule's scope reading nothing and took its
/// objects out of scope. It is now refused, as a whole and before anything persists, while a Scoping Criterion or an
/// Expression reads a removed attribute. Real PostgreSQL, because the readers are found by a projection the
/// in-memory provider would not exercise.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SchemaRefreshRemovalReaderRefusalDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL schema refresh reader tests.");

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

    [TestCase(false, TestName = "ApplyWithRemoval_ScopingCriterionComparingARemovedAttribute_RefusesBeforeAnythingPersistsAsync")]
    [TestCase(true, TestName = "ApplyWithRemoval_ScopingCriterionExpressionReadingARemovedAttribute_RefusesBeforeAnythingPersistsAsync")]
    public async Task ApplyWithRemoval_ReaderOfARemovedAttribute_RefusesBeforeAnythingPersistsAsync(bool viaExpression)
    {
        int connectedSystemId, userTypeId, departmentId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
            var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var department = new ConnectedSystemObjectTypeAttribute { Name = "department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = userType, Selected = true };
            userType.Attributes.Add(department);
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };

            var rule = new SyncRule
            {
                Name = "HR Users Inbound",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystem = system,
                ConnectedSystemObjectType = userType,
                MetaverseObjectType = personType
            };
            rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria =
                [
                    viaExpression
                        ? new SyncRuleScopingCriteria { Expression = "Lower(cs[\"Department\"])", CompareAs = AttributeDataType.Text, ComparisonType = SearchComparisonType.StartsWith, StringValue = "fin" }
                        : new SyncRuleScopingCriteria { ConnectedSystemAttribute = department, ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" }
                ]
            });

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.MetaverseObjectTypes.Add(personType);
            seed.SyncRules.Add(rule);
            await seed.SaveChangesAsync();
            connectedSystemId = system.Id;
            userTypeId = userType.Id;
            departmentId = department.Id;
        }

        // The refresh as a preview would report it: the Connector no longer reports "department".
        var previewResult = new SchemaRefreshResult { Success = true };
        previewResult.RemovedAttributes["user"] = ["department"];
        previewResult.PreRefreshSchema =
        [
            new SchemaRefreshPreRefreshType
            {
                Id = userTypeId,
                Name = "user",
                Attributes = [new SchemaRefreshPreRefreshAttribute { Id = departmentId, Name = "department" }]
            }
        ];

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var connectedSystem = await jim.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
            var dependents = await jim.ConnectedSystems.DetectSchemaRefreshDependentsAsync(connectedSystemId, previewResult);
            Assert.That(dependents.RemovalRefused, Is.True, "the preview names the reader");

            var refusal = Assert.ThrowsAsync<AttributeReadByConfigurationException>(() =>
                jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshWithRemovalAsync(connectedSystem!, previewResult, new SchemaRefreshDependents(), TestUtilities.GetInitiatedBy()));
            Assert.That(refusal!.Message, Does.Contain("HR Users Inbound"));
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.SyncRuleScopingCriteria.CountAsync(), Is.EqualTo(1), "the Scoping Criterion is kept");
            Assert.That(await verify.ConnectedSystemAttributes.AnyAsync(a => a.Id == departmentId), Is.True, "the attribute row is kept");
            Assert.That(await verify.Activities.AnyAsync(), Is.False, "nothing was recorded: the refusal came before the refresh was applied");
            Assert.That(await verify.WorkerTasks.AnyAsync(), Is.False, "no data removal was queued");
        }
    }
}
