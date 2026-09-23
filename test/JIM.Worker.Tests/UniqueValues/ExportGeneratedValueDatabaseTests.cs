// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Real-PostgreSQL round trip for export-mode Unique Value Generation (#242, Phase 2 work package H): proves
/// the assignment's foreign key to <see cref="ConnectedSystemObject"/> (never <c>MetaverseObject</c>) persists
/// and reads back correctly. The workflow tests (<c>ExportGeneratedValueWorkflowTests</c>) cover the resolve
/// and commit behaviour itself against the in-memory repository; this is the one thing only a real provider
/// proves for export mode specifically, mirroring <c>UniqueValueGenerationDatabaseTests</c>' import-mode
/// coverage.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportGeneratedValueDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export-mode Unique Value Generation tests.");

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
        .Options);

    private static SyncRepository NewSyncRepository(JimDbContext ctx) => new(new PostgresDataRepository(ctx));

    private sealed record Estate(int ConnectedSystemId, int ConnectedSystemObjectTypeId, int LoginNameAttributeId, SyncRuleMappingGeneration Generation);

    /// <summary>
    /// A Connected System, one Text Connected System Object Type attribute, and an export Synchronisation Rule
    /// mapping with a Generation row targeting it, all suffixed so parallel test runs never collide.
    /// </summary>
    private async Task<Estate> SeedEstateAsync(string suffix)
    {
        await using var ctx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var system = new ConnectedSystem { Name = $"Ticketing-{suffix}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        ctx.AddRange(connectorDefinition, system, csType, mvoType);
        await ctx.SaveChangesAsync();

        var loginNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = $"loginName-{suffix}", Type = AttributeDataType.Text, Selected = true, ConnectedSystemObjectType = csType
        };
        ctx.ConnectedSystemAttributes.Add(loginNameAttribute);
        await ctx.SaveChangesAsync();

        var exportRule = new SyncRule
        {
            Name = $"export-{suffix}", Direction = SyncRuleDirection.Export, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.Add(exportRule);
        await ctx.SaveChangesAsync();

        var mapping = new SyncRuleMapping { SyncRuleId = exportRule.Id, TargetConnectedSystemAttributeId = loginNameAttribute.Id };
        ctx.SyncRuleMappings.Add(mapping);
        await ctx.SaveChangesAsync();

        var generation = new SyncRuleMappingGeneration { SyncRuleMappingId = mapping.Id, TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
        ctx.SyncRuleMappingGenerations.Add(generation);
        await ctx.SaveChangesAsync();

        return new Estate(system.Id, csType.Id, loginNameAttribute.Id, generation);
    }

    /// <summary>
    /// A bare, otherwise-empty Connected System Object: what the worker would have persisted, in the same page
    /// flush, before calling <see cref="UniqueValueGenerationServer.CommitAssignmentsAsync"/> with its real id
    /// (<c>FlushPendingExportOperationsAsync</c>, after the provisioning Connected System Objects block). The
    /// assignment's foreign key requires the row to already exist.
    /// </summary>
    private async Task<Guid> CreateBareCsoAsync(Estate estate)
    {
        await using var ctx = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = estate.ConnectedSystemId,
            TypeId = estate.ConnectedSystemObjectTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        ctx.ConnectedSystemObjects.Add(cso);
        await ctx.SaveChangesAsync();
        return cso.Id;
    }

    [Test]
    public async Task ResolveAndCommit_ExportModeGeneratedValue_PersistsAssignmentKeyedOnTheConnectedSystemObjectAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var csoId = await CreateBareCsoAsync(estate);

        await using var ctx = NewContext();
        var repository = NewSyncRepository(ctx);
        var server = new UniqueValueGenerationServer(repository);
        var options = new UniqueValueResolveOptions { Reservations = new UniqueValueReservationSet(), ReservationOwnerId = Guid.NewGuid() };

        var request = new GenerationRequest
        {
            Mode = GeneratedValueMode.Export,
            ConnectedSystemObjectId = csoId,
            ConnectedSystemObjectTypeAttributeId = estate.LoginNameAttributeId,
            Generation = estate.Generation,
            TargetType = AttributeDataType.Text,
            AttributeName = "loginName",
            BaseValue = "e1",
            CallerState = csoId
        };

        var outcomes = await server.ResolveAsync([request], options);
        Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));

        var losers = await server.CommitAssignmentsAsync(outcomes, r => (Guid)r.CallerState!);
        Assert.That(losers, Is.Empty);

        var persisted = await repository.GetGeneratedValueAssignmentForConnectedSystemObjectAsync(csoId, estate.LoginNameAttributeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted, Is.Not.Null);
            Assert.That(persisted!.Value, Is.EqualTo("e1"));
            Assert.That(persisted.ConnectedSystemObjectId, Is.EqualTo(csoId));
            Assert.That(persisted.MetaverseObjectId, Is.Null, "export-mode assignments never touch the Metaverse");
        }
    }

    [Test]
    public async Task CommitAssignmentsAsync_ExportModeWithNoExistingConnectedSystemObjectRow_FailsTheForeignKeyAsync()
    {
        // Synchronisation Integrity: committing before FlushPendingExportOperationsAsync has persisted the
        // provisioning Connected System Object must fail hard rather than silently write an orphaned row.
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var neverPersistedCsoId = Guid.NewGuid();

        await using var ctx = NewContext();
        var repository = NewSyncRepository(ctx);
        var server = new UniqueValueGenerationServer(repository);
        var options = new UniqueValueResolveOptions { Reservations = new UniqueValueReservationSet(), ReservationOwnerId = Guid.NewGuid() };

        var request = new GenerationRequest
        {
            Mode = GeneratedValueMode.Export,
            ConnectedSystemObjectId = neverPersistedCsoId,
            ConnectedSystemObjectTypeAttributeId = estate.LoginNameAttributeId,
            Generation = estate.Generation,
            TargetType = AttributeDataType.Text,
            AttributeName = "loginName",
            BaseValue = "e2",
            CallerState = neverPersistedCsoId
        };

        var outcomes = await server.ResolveAsync([request], options);

        Assert.That(async () => await server.CommitAssignmentsAsync(outcomes, r => (Guid)r.CallerState!),
            Throws.Exception);
    }
}
