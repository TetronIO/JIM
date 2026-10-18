// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Real-PostgreSQL cover for the two <c>PUT .../matching-rules/{ruleId}</c> endpoints, which
/// <c>Set-JIMMatchingRule</c> calls. Each request runs on a fresh NoTracking context, as JIM.Web does, so a change the
/// endpoint reports but does not save shows up as a difference between the response and the database.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SynchronisationControllerMatchingRuleUpdateDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // A database of this assembly's own: the Worker suite runs at the same time in the database-tests job, and
        // migrating or emptying a shared database under these tests broke them.
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment(databaseSuffix: "_web_api");
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL matching-rule endpoint tests.");
        _connectionString = connectionString!;
        PostgresTestDatabase.CreateDatabaseIfMissing(_connectionString);

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private sealed record Seeded(int ConnectedSystemId, int ConnectedSystemRuleId, int SyncRuleId, int SyncRuleRuleId, int EmployeeIdAttributeId, int MailAttributeId, Guid ApiKeyId);

    /// <summary>
    /// A Connected System Object Type with two Text attributes, one rule on the type matching on the first, and an
    /// import Synchronisation Rule holding a rule of its own on the same attribute.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"omr-endpoint-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = $"omr-endpoint-system-{suffix}", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        ctx.ConnectedSystemObjectTypes.Add(objectType);
        await ctx.SaveChangesAsync();

        var employeeId = new ConnectedSystemObjectTypeAttribute { ConnectedSystemObjectType = objectType, Name = "employeeId", Type = AttributeDataType.Text, Selected = true };
        var mail = new ConnectedSystemObjectTypeAttribute { ConnectedSystemObjectType = objectType, Name = "mail", Type = AttributeDataType.Text, Selected = true };
        ctx.ConnectedSystemAttributes.AddRange(employeeId, mail);
        await ctx.SaveChangesAsync();

        var target = new MetaverseAttribute { Name = $"omr-endpoint-target-{suffix}", Type = AttributeDataType.Text };
        var mvoType = new MetaverseObjectType { Name = $"omr-endpoint-person-{suffix}", PluralName = $"omr-endpoint-people-{suffix}", Attributes = [target] };
        ctx.MetaverseObjectTypes.Add(mvoType);
        await ctx.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = $"omr-endpoint-import-{suffix}",
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = mvoType.Id,
            Direction = SyncRuleDirection.Import
        };
        ctx.SyncRules.Add(syncRule);
        await ctx.SaveChangesAsync();

        var typeRule = new ObjectMatchingRule
        {
            Order = 0,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = mvoType.Id,
            TargetMetaverseAttributeId = target.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = employeeId.Id }]
        };
        var syncRuleRule = new ObjectMatchingRule
        {
            Order = 0,
            SyncRuleId = syncRule.Id,
            TargetMetaverseAttributeId = target.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = employeeId.Id }]
        };
        ctx.ObjectMatchingRules.AddRange(typeRule, syncRuleRule);

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(), Name = $"omr-endpoint-key-{suffix}", KeyHash = "hash", KeyPrefix = "test", IsEnabled = true, Created = DateTime.UtcNow
        };
        ctx.ApiKeys.Add(apiKey);
        await ctx.SaveChangesAsync();

        return new Seeded(system.Id, typeRule.Id, syncRule.Id, syncRuleRule.Id, employeeId.Id, mail.Id, apiKey.Id);
    }

    /// <summary>Runs <paramref name="call"/> on a controller whose application has a context of its own, as a request does.</summary>
    private async Task<IActionResult> CallAsync(Guid apiKeyId, Func<SynchronisationController, Task<IActionResult>> call)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        using var application = new JimApplication(repository, syncRepository: repository.Sync);
        var controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new List<Claim> { new("auth_method", "api_key"), new(ClaimTypes.NameIdentifier, apiKeyId.ToString()) }, "TestAuth"))
                }
            }
        };
        return await call(controller);
    }

    private async Task<List<ObjectMatchingRuleSource>> StoredSourcesAsync(int ruleId)
    {
        await using var ctx = NewContext();
        return await ctx.ObjectMatchingRuleSources.Where(s => s.ObjectMatchingRuleId == ruleId).ToListAsync();
    }

    private static UpdateObjectMatchingRuleRequest ExpressionSource(string expression) => new()
    {
        Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = expression }]
    };

    [Test]
    public async Task UpdateObjectMatchingRuleAsync_SourceReplacedByAnExpression_SavesTheSourceTheResponseReportsAsync()
    {
        var seeded = await SeedAsync();
        const string expression = "Trim(cs[\"employeeId\"])";

        var result = await CallAsync(seeded.ApiKeyId, controller =>
            controller.UpdateObjectMatchingRuleAsync(seeded.ConnectedSystemId, seeded.ConnectedSystemRuleId, ExpressionSource(expression)));

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), (result as ObjectResult)?.Value?.ToString());
        var stored = await StoredSourcesAsync(seeded.ConnectedSystemRuleId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Select(s => s.Expression), Is.EqualTo(new[] { expression }));
            Assert.That(stored.Single().ConnectedSystemAttributeId, Is.Null, "the attribute source the request replaced must be gone");
        }
    }

    [Test]
    public async Task UpdateSyncRuleObjectMatchingRuleAsync_SourceReplacedByAnotherAttribute_SavesTheSourceTheResponseReportsAsync()
    {
        var seeded = await SeedAsync();
        var request = new UpdateObjectMatchingRuleRequest
        {
            Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, ConnectedSystemAttributeId = seeded.MailAttributeId }]
        };

        var result = await CallAsync(seeded.ApiKeyId, controller =>
            controller.UpdateSyncRuleObjectMatchingRuleAsync(seeded.SyncRuleId, seeded.SyncRuleRuleId, request));

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), (result as ObjectResult)?.Value?.ToString());
        var stored = await StoredSourcesAsync(seeded.SyncRuleRuleId);
        Assert.That(stored.Select(s => s.ConnectedSystemAttributeId), Is.EqualTo(new int?[] { seeded.MailAttributeId }));
    }

    [Test]
    public async Task UpdateObjectMatchingRuleAsync_SourcesOmitted_KeepsTheStoredSourceAsync()
    {
        var seeded = await SeedAsync();

        var result = await CallAsync(seeded.ApiKeyId, controller =>
            controller.UpdateObjectMatchingRuleAsync(seeded.ConnectedSystemId, seeded.ConnectedSystemRuleId, new UpdateObjectMatchingRuleRequest { CaseSensitive = false }));

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), (result as ObjectResult)?.Value?.ToString());
        var stored = await StoredSourcesAsync(seeded.ConnectedSystemRuleId);
        Assert.That(stored.Select(s => s.ConnectedSystemAttributeId), Is.EqualTo(new int?[] { seeded.EmployeeIdAttributeId }));
    }

    [Test]
    public async Task UpdateSyncRuleObjectMatchingRuleAsync_CaseSensitivityChanged_SavesItAsync()
    {
        // Loading the Synchronisation Rule tracks its Object Matching Rules, so any update on this endpoint once
        // collided with the rule loaded beside it and failed.
        var seeded = await SeedAsync();
        bool before;
        await using (var ctx = NewContext())
            before = (await ctx.ObjectMatchingRules.SingleAsync(r => r.Id == seeded.SyncRuleRuleId)).CaseSensitive;

        var result = await CallAsync(seeded.ApiKeyId, controller =>
            controller.UpdateSyncRuleObjectMatchingRuleAsync(seeded.SyncRuleId, seeded.SyncRuleRuleId, new UpdateObjectMatchingRuleRequest { CaseSensitive = !before }));

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), (result as ObjectResult)?.Value?.ToString());
        await using var assertCtx = NewContext();
        var saved = await assertCtx.ObjectMatchingRules.Include(r => r.Sources).SingleAsync(r => r.Id == seeded.SyncRuleRuleId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved.CaseSensitive, Is.EqualTo(!before));
            Assert.That(saved.Sources.Select(s => s.ConnectedSystemAttributeId), Is.EqualTo(new int?[] { seeded.EmployeeIdAttributeId }));
        }
    }
}
