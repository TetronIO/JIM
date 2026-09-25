// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of
/// <see cref="JIM.PostgresData.Repositories.ConnectedSystemRepository.GetPendingExportsForConfirmationEvaluationAsync"/>,
/// the lean fetch that replaced <c>GetPendingExportsAsync</c> for the sync processors' upfront Pending
/// Export confirmation load (<c>SyncFullSyncTaskProcessor</c>/<c>SyncDeltaSyncTaskProcessor</c>).
/// <see cref="JIM.Application.Servers.SyncEngine.EvaluatePendingExportConfirmation"/> skips every Pending
/// Export whose Status is Pending or Exported unconditionally (the vast majority at scale) and never
/// reads a Pending Export's own ConnectedSystemObject navigation - it is handed the Connected System
/// Object being evaluated separately. The old method loaded that navigation, plus its AttributeValues,
/// for every Pending Export in the Connected System; at 100,000 Connected System Objects that took 35
/// seconds. This method filters the skipped statuses in SQL and never loads the Connected System Object
/// graph at all.
/// </summary>
/// <remarks>
/// Seeding pattern mirrors <see cref="ExportMatchCandidateDatabaseTests"/>: every entity a test writes is
/// created and saved through ONE <c>seed</c> context per test, never split across separately-disposed
/// contexts, because EF's <c>Add()</c>/<c>AddRange()</c> walks the whole reachable entity graph and
/// (re-)inserts every untracked navigation it finds (`src/CLAUDE.md` &gt; "DbSet.Add Walks the Graph").
/// Only the read-only call under test uses a second, fresh context.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportConfirmationEvaluationFetchDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export confirmation-evaluation fetch tests.");

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

    /// <summary>
    /// Seeds one Pending Export per <see cref="PendingExportStatus"/> value for the subject Connected
    /// System (each with its own Connected System Object, plus an AttributeValueChange with its
    /// Attribute), one further Pending Export with no linked Connected System Object, and one qualifying
    /// Pending Export on a second Connected System - all on one context (see class remarks).
    /// </summary>
    private async Task<(int SubjectSystemId, Dictionary<PendingExportStatus, Guid> IdsByStatus, Guid NullCsoPeId, Guid OtherSystemPeId)>
        SeedAcrossStatusesAndSystemsAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var subjectSystem = new ConnectedSystem { Name = "Yellowstone Target", ConnectorDefinition = connectorDefinition };
        var otherSystem = new ConnectedSystem { Name = "Glitterband Target", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = subjectSystem, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(displayNameAttr);

        seed.AddRange(connectorDefinition, subjectSystem, otherSystem, csType);
        await seed.SaveChangesAsync();

        var idsByStatus = new Dictionary<PendingExportStatus, Guid>();
        foreach (var status in Enum.GetValues<PendingExportStatus>())
        {
            var cso = new ConnectedSystemObject { Type = csType, ConnectedSystem = subjectSystem, Status = ConnectedSystemObjectStatus.Normal };
            seed.Add(cso);
            await seed.SaveChangesAsync();

            var pe = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = subjectSystem,
                ConnectedSystemId = subjectSystem.Id,
                ConnectedSystemObjectId = cso.Id,
                ChangeType = PendingExportChangeType.Update,
                Status = status,
                CreatedAt = DateTime.UtcNow
            };
            pe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                Attribute = displayNameAttr,
                AttributeId = displayNameAttr.Id,
                StringValue = $"Value for {status}",
                ChangeType = PendingExportAttributeChangeType.Update
            });
            seed.Add(pe);
            await seed.SaveChangesAsync();

            idsByStatus[status] = pe.Id;
        }

        // A Pending Export with no linked Connected System Object (awaiting reference resolution, say):
        // it can never be indexed by CSO ID for the confirmation lookup, so it must be excluded even
        // though its Status otherwise qualifies.
        var nullCsoPe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = subjectSystem,
            ConnectedSystemId = subjectSystem.Id,
            ConnectedSystemObjectId = null,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.ExportNotConfirmed,
            CreatedAt = DateTime.UtcNow
        };
        seed.Add(nullCsoPe);
        await seed.SaveChangesAsync();

        // A qualifying Pending Export belonging to a DIFFERENT Connected System: must be excluded by
        // ConnectedSystemId, not just by status.
        var otherSystemCso = new ConnectedSystemObject { Type = csType, ConnectedSystem = otherSystem, Status = ConnectedSystemObjectStatus.Normal };
        seed.Add(otherSystemCso);
        await seed.SaveChangesAsync();

        var otherSystemPe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = otherSystem,
            ConnectedSystemId = otherSystem.Id,
            ConnectedSystemObjectId = otherSystemCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.ExportNotConfirmed,
            CreatedAt = DateTime.UtcNow
        };
        seed.Add(otherSystemPe);
        await seed.SaveChangesAsync();

        return (subjectSystem.Id, idsByStatus, nullCsoPe.Id, otherSystemPe.Id);
    }

    [Test]
    public async Task GetPendingExportsForConfirmationEvaluationAsync_ReturnsOnlyNonPendingNonExportedWithACsoForThatSystemAsync()
    {
        var (subjectSystemId, idsByStatus, nullCsoPeId, otherSystemPeId) = await SeedAcrossStatusesAndSystemsAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetPendingExportsForConfirmationEvaluationAsync(subjectSystemId);

        var expectedStatuses = Enum.GetValues<PendingExportStatus>()
            .Where(s => s != PendingExportStatus.Pending && s != PendingExportStatus.Exported)
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(expectedStatuses.Count),
                "Only the non-Pending, non-Exported, CSO-linked Pending Exports for the subject system should return.");

            foreach (var status in expectedStatuses)
                Assert.That(result.Any(pe => pe.Id == idsByStatus[status]), Is.True, $"Expected a Pending Export with status {status}.");

            Assert.That(result.Any(pe => pe.Id == idsByStatus[PendingExportStatus.Pending]), Is.False,
                "Pending status is skipped unconditionally by EvaluatePendingExportConfirmation.");
            Assert.That(result.Any(pe => pe.Id == idsByStatus[PendingExportStatus.Exported]), Is.False,
                "Exported status is skipped unconditionally by EvaluatePendingExportConfirmation.");
            Assert.That(result.Any(pe => pe.Id == nullCsoPeId), Is.False,
                "A Pending Export with no linked Connected System Object cannot be indexed by CSO ID.");
            Assert.That(result.Any(pe => pe.Id == otherSystemPeId), Is.False,
                "Must not return Pending Exports belonging to a different Connected System.");
        }
    }

    [Test]
    public async Task GetPendingExportsForConfirmationEvaluationAsync_LoadsAttributeValueChangesAndAttributeButNotConnectedSystemObjectAsync()
    {
        var (subjectSystemId, idsByStatus, _, _) = await SeedAcrossStatusesAndSystemsAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetPendingExportsForConfirmationEvaluationAsync(subjectSystemId);
        var failedPe = result.Single(pe => pe.Id == idsByStatus[PendingExportStatus.Failed]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failedPe.AttributeValueChanges, Has.Count.EqualTo(1),
                "AttributeValueChanges must be loaded: SyncEngine.EvaluatePendingExportConfirmation reads them.");
            Assert.That(failedPe.AttributeValueChanges.Single().Attribute, Is.Not.Null,
                "Each AttributeValueChange's Attribute must be loaded.");
            Assert.That(failedPe.ConnectedSystemObject, Is.Null,
                "The Connected System Object graph must NOT be loaded: EvaluatePendingExportConfirmation is handed the CSO separately.");
        }
    }
}
