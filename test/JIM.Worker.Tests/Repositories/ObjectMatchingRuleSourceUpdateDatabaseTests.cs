// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for changing an Object Matching Rule's source through the REST API and PowerShell
/// (<c>PUT .../matching-rules/{ruleId}</c>, <c>Set-JIMMatchingRule -SourceAttributeId</c> or <c>-SourceExpression</c>).
/// <para>
/// JIM.Web loads the rule untracked, the controller replaces its sources in memory, and the update saves it. Marking
/// only the rule itself modified saved its own columns and none of the change to its sources, so the API answered 200
/// with the new source while the old one stayed in the database. Only a real provider on a NoTracking context, as
/// JIM.Web runs, can see this; the in-memory provider tracks by default and the unit fixtures mock the repository.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ObjectMatchingRuleSourceUpdateDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL matching-rule source update tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private sealed record Seeded(int RuleId, int EmployeeIdAttributeId, int MailAttributeId, Guid ApiKeyId);

    /// <summary>A Connected System Object Type with two Text attributes and one rule matching on the first.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"omr-source-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = $"omr-source-system-{suffix}", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        ctx.ConnectedSystemObjectTypes.Add(objectType);
        await ctx.SaveChangesAsync();

        var employeeId = new ConnectedSystemObjectTypeAttribute { ConnectedSystemObjectType = objectType, Name = "employeeId", Type = AttributeDataType.Text, Selected = true };
        var mail = new ConnectedSystemObjectTypeAttribute { ConnectedSystemObjectType = objectType, Name = "mail", Type = AttributeDataType.Text, Selected = true };
        ctx.ConnectedSystemAttributes.AddRange(employeeId, mail);
        await ctx.SaveChangesAsync();

        var target = new MetaverseAttribute { Name = $"omr-source-target-{suffix}", Type = AttributeDataType.Text };
        var mvoType = new MetaverseObjectType { Name = $"omr-source-person-{suffix}", PluralName = $"omr-source-people-{suffix}", Attributes = [target] };
        ctx.MetaverseObjectTypes.Add(mvoType);
        await ctx.SaveChangesAsync();

        var rule = new ObjectMatchingRule
        {
            Order = 0,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = mvoType.Id,
            TargetMetaverseAttributeId = target.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = employeeId.Id }]
        };
        ctx.ObjectMatchingRules.Add(rule);

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(), Name = $"omr-source-key-{suffix}", KeyHash = "hash", KeyPrefix = "test", IsEnabled = true, Created = DateTime.UtcNow
        };
        ctx.ApiKeys.Add(apiKey);
        await ctx.SaveChangesAsync();

        return new Seeded(rule.Id, employeeId.Id, mail.Id, apiKey.Id);
    }

    /// <summary>
    /// As the REST API does it: load the rule, replace its sources with new ones, and save it through the application
    /// layer on a NoTracking context.
    /// </summary>
    private async Task ReplaceSourcesAsync(Seeded seeded, Func<ObjectMatchingRule, ObjectMatchingRuleSource> newSource)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        using var application = new JimApplication(repository, syncRepository: repository.Sync);
        var rule = await application.ConnectedSystems.GetObjectMatchingRuleAsync(seeded.RuleId);
        Assert.That(rule, Is.Not.Null);
        var apiKey = await application.Repository.ApiKeys.GetByIdAsync(seeded.ApiKeyId);

        rule!.Sources.Clear();
        rule.Sources.Add(newSource(rule));
        await application.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, apiKey!);
    }

    [Test]
    public async Task UpdateObjectMatchingRuleAsync_AttributeSourceReplacedByAnExpression_PersistsTheExpressionAloneAsync()
    {
        var seeded = await SeedAsync();

        await ReplaceSourcesAsync(seeded, rule => new ObjectMatchingRuleSource
        {
            Order = 0, ObjectMatchingRuleId = rule.Id, Expression = "Trim(cs[\"employeeId\"])"
        });

        await using var assertCtx = NewContext();
        var sources = await assertCtx.ObjectMatchingRuleSources.Where(s => s.ObjectMatchingRuleId == seeded.RuleId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sources, Has.Count.EqualTo(1), "the rule has exactly one source after the change");
            Assert.That(sources[0].Expression, Is.EqualTo("Trim(cs[\"employeeId\"])"));
            Assert.That(sources[0].ConnectedSystemAttributeId, Is.Null, "the attribute source it replaced must be gone");
        }
    }

    [Test]
    public async Task UpdateObjectMatchingRuleAsync_AttributeSourceReplacedByAnotherAttribute_PersistsTheNewAttributeAsync()
    {
        var seeded = await SeedAsync();

        await ReplaceSourcesAsync(seeded, rule => new ObjectMatchingRuleSource
        {
            Order = 0, ObjectMatchingRuleId = rule.Id, ConnectedSystemAttributeId = seeded.MailAttributeId
        });

        await using var assertCtx = NewContext();
        var sources = await assertCtx.ObjectMatchingRuleSources.Where(s => s.ObjectMatchingRuleId == seeded.RuleId).ToListAsync();
        Assert.That(sources.Select(s => s.ConnectedSystemAttributeId), Is.EqualTo(new int?[] { seeded.MailAttributeId }));
    }

    [Test]
    public async Task UpdateObjectMatchingRuleAsync_SourcesUnchanged_KeepsThemAndSavesTheRulesOwnColumnsAsync()
    {
        // The portal's reorder and case-sensitivity edits save the rule with the sources it was loaded with.
        var seeded = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            using var application = new JimApplication(repository, syncRepository: repository.Sync);
            var rule = await application.ConnectedSystems.GetObjectMatchingRuleAsync(seeded.RuleId);
            var apiKey = await application.Repository.ApiKeys.GetByIdAsync(seeded.ApiKeyId);
            rule!.CaseSensitive = !rule.CaseSensitive;
            await application.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, apiKey!);
        }

        await using var assertCtx = NewContext();
        var saved = await assertCtx.ObjectMatchingRules.Include(r => r.Sources).SingleAsync(r => r.Id == seeded.RuleId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved.Sources.Select(s => s.ConnectedSystemAttributeId), Is.EqualTo(new int?[] { seeded.EmployeeIdAttributeId }));
            Assert.That(saved.CaseSensitive, Is.Not.EqualTo(ObjectMatchingDefaults.CaseSensitive));
        }
    }

    [Test]
    public async Task UpdateObjectMatchingRuleAsync_KeptSourceEditedInPlace_PersistsTheEditAsync()
    {
        // A caller may edit the source the rule already holds rather than replace it; the edit must not be dropped.
        var seeded = await SeedAsync();
        int sourceId;

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            using var application = new JimApplication(repository, syncRepository: repository.Sync);
            var rule = await application.ConnectedSystems.GetObjectMatchingRuleAsync(seeded.RuleId);
            var apiKey = await application.Repository.ApiKeys.GetByIdAsync(seeded.ApiKeyId);
            var source = rule!.Sources.Single();
            sourceId = source.Id;
            source.ConnectedSystemAttributeId = null;
            source.ConnectedSystemAttribute = null;
            source.Expression = "Trim(cs[\"employeeId\"])";
            await application.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, apiKey!);
        }

        await using var assertCtx = NewContext();
        var sources = await assertCtx.ObjectMatchingRuleSources.Where(s => s.ObjectMatchingRuleId == seeded.RuleId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sources.Select(s => s.Id), Is.EqualTo(new[] { sourceId }), "the source is kept, not replaced");
            Assert.That(sources[0].Expression, Is.EqualTo("Trim(cs[\"employeeId\"])"));
            Assert.That(sources[0].ConnectedSystemAttributeId, Is.Null);
        }
    }
}
