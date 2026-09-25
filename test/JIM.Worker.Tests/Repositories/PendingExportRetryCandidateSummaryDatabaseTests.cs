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
/// Real-PostgreSQL verification of the lean Summary-tier projection added to the Full Import unseen
/// exported-Create retry step (<c>SyncImportTaskProcessor.RetryUnconfirmedExportedCreatesAsync</c>):
/// <c>ConnectedSystemRepository.GetExportedCreatePendingExportRetryCandidateSummariesAsync</c>. The
/// production step previously loaded the FULL Pending Export / Connected System Object / attribute-value
/// graph for every exported Create Pending Export on a Pending Provisioning object, even though in the
/// ordinary case every one of them is confirmed by the very next Full Import; measured at 25 seconds for
/// 100,000 already-confirmed candidates. This projection returns only the Pending Export id, the
/// Connected System Object id, and its primary External Id value as typed nullable columns, so the caller
/// can decide "was this candidate seen this run?" in memory before promoting only the genuinely unseen
/// ones to the full graph load.
/// <para>
/// Only a real database can prove the join text: the LEFT JOIN to the External Id attribute value row,
/// the eligibility predicate (Connected System, Create, Status Exported, Connected System Object type and
/// status, optional partition), and that a Connected System Object with no External Id value row yields
/// nulls rather than being dropped. The in-memory provider does not exercise raw SQL at all.
/// </para>
/// </summary>
/// <remarks>
/// Seeding pattern mirrors <see cref="ExportMatchCandidateDatabaseTests"/>: every entity a test writes
/// (Connector Definition, Connected System, Object Type, Attribute, Connected System Object, Pending
/// Export) is created and saved through ONE <c>seed</c> context per test, never split across
/// separately-disposed contexts. EF's <c>Add()</c>/<c>AddRange()</c> walks the whole reachable entity
/// graph and (re-)inserts every navigation it finds untracked (`src/CLAUDE.md` &gt; "DbSet.Add Walks the
/// Graph"); a detached, already-persisted parent obtained from a disposed context is exactly such an
/// untracked navigation, so adding a child that still references it re-attempts the parent's insert and
/// fails with a duplicate key on its primary key. Keeping the whole seed on one context means a later
/// `Add()` finds the parent already tracked (Unchanged) and stops the walk there. Only the read-only call
/// under test uses a second, fresh context, matching how the repository is actually used in production.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportRetryCandidateSummaryDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export retry candidate summary tests.");

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

    /// <summary>
    /// Builds and saves a Connected System and a single Object Type carrying one External Id attribute of
    /// the given data type, on the caller's own context. The caller must use the SAME context for any
    /// further entity creation that references the returned entities (see the class remarks).
    /// </summary>
    private static async Task<(ConnectedSystem System, ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute ExternalIdAttribute)> SeedSystemAndTypeAsync(
        JimDbContext seed, AttributeDataType externalIdType = AttributeDataType.Text, string systemName = "Yellowstone Target")
    {
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid()}", BuiltIn = true };
        var system = new ConnectedSystem { Name = systemName, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId",
            ConnectedSystemObjectType = csType,
            Type = externalIdType,
            AttributePlurality = AttributePlurality.SingleValued,
            IsExternalId = true,
            Selected = true
        };
        csType.Attributes.Add(externalIdAttribute);

        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        return (system, csType, externalIdAttribute);
    }

    /// <summary>
    /// Builds (but does not save) a Pending Provisioning Connected System Object, optionally carrying an
    /// External Id attribute value of the given typed value. A null value leaves the object with no
    /// attribute values at all, exercising the LEFT JOIN's "no row yet" case.
    /// </summary>
    private static ConnectedSystemObject CreatePendingProvisioningCso(
        ConnectedSystem system,
        ConnectedSystemObjectType type,
        ConnectedSystemObjectTypeAttribute externalIdAttribute,
        object? externalIdValue,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.PendingProvisioning)
    {
        var cso = new ConnectedSystemObject
        {
            Type = type,
            ConnectedSystem = system,
            Status = status,
            ExternalIdAttributeId = externalIdAttribute.Id
        };

        if (externalIdValue != null)
        {
            var av = new ConnectedSystemObjectAttributeValue { Attribute = externalIdAttribute };
            switch (externalIdValue)
            {
                case string s: av.StringValue = s; break;
                case int i: av.IntValue = i; break;
                case long l: av.LongValue = l; break;
                case decimal d: av.DecimalValue = d; break;
                case Guid g: av.GuidValue = g; break;
            }
            cso.AttributeValues.Add(av);
        }

        return cso;
    }

    private static PendingExport CreateExportedCreate(
        ConnectedSystem system,
        ConnectedSystemObject cso,
        PendingExportChangeType changeType = PendingExportChangeType.Create,
        PendingExportStatus status = PendingExportStatus.Exported)
    {
        return new PendingExport
        {
            ConnectedSystemId = system.Id,
            ConnectedSystemObject = cso,
            ChangeType = changeType,
            Status = status
        };
    }

    #region One row per supported typed External Id, plus the no-value case

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_TextExternalId_ReturnsStringValueAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);
        var cso = CreatePendingProvisioningCso(system, type, attribute, "EMP0001");
        var pendingExport = CreateExportedCreate(system, cso);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id);

        Assert.That(result, Has.Count.EqualTo(1));
        var summary = result[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.PendingExportId, Is.EqualTo(pendingExport.Id));
            Assert.That(summary.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(summary.ExternalIdStringValue, Is.EqualTo("EMP0001"));
            Assert.That(summary.ExternalIdIntValue, Is.Null);
            Assert.That(summary.ExternalIdLongValue, Is.Null);
            Assert.That(summary.ExternalIdDecimalValue, Is.Null);
            Assert.That(summary.ExternalIdGuidValue, Is.Null);
        }
    }

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_GuidExternalId_ReturnsGuidValueAsync()
    {
        var externalId = Guid.NewGuid();

        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Guid);
        var cso = CreatePendingProvisioningCso(system, type, attribute, externalId);
        var pendingExport = CreateExportedCreate(system, cso);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id);

        Assert.That(result, Has.Count.EqualTo(1));
        var summary = result[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.PendingExportId, Is.EqualTo(pendingExport.Id));
            Assert.That(summary.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(summary.ExternalIdGuidValue, Is.EqualTo(externalId));
            Assert.That(summary.ExternalIdStringValue, Is.Null);
            Assert.That(summary.ExternalIdIntValue, Is.Null);
            Assert.That(summary.ExternalIdLongValue, Is.Null);
            Assert.That(summary.ExternalIdDecimalValue, Is.Null);
        }
    }

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_NoExternalIdValueRow_ReturnsAllNullTypedColumnsAsync()
    {
        // Legitimate state: the Create has been exported (it has an External Id ATTRIBUTE configured),
        // but the object has no External Id VALUE yet - nothing has confirmed one. The candidate must
        // still be returned (the LEFT JOIN must not drop it), just with every typed column null.
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);
        var cso = CreatePendingProvisioningCso(system, type, attribute, externalIdValue: null);
        var pendingExport = CreateExportedCreate(system, cso);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id);

        Assert.That(result, Has.Count.EqualTo(1));
        var summary = result[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.PendingExportId, Is.EqualTo(pendingExport.Id));
            Assert.That(summary.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(summary.ExternalIdStringValue, Is.Null);
            Assert.That(summary.ExternalIdIntValue, Is.Null);
            Assert.That(summary.ExternalIdLongValue, Is.Null);
            Assert.That(summary.ExternalIdDecimalValue, Is.Null);
            Assert.That(summary.ExternalIdGuidValue, Is.Null);
        }
    }

    #endregion

    #region Eligibility exclusions

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_UpdateChangeType_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);
        var cso = CreatePendingProvisioningCso(system, type, attribute, "EMP0001");
        var pendingExport = CreateExportedCreate(system, cso, changeType: PendingExportChangeType.Update);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_PendingStatus_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);
        var cso = CreatePendingProvisioningCso(system, type, attribute, "EMP0001");
        var pendingExport = CreateExportedCreate(system, cso, status: PendingExportStatus.Pending);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_CsoStatusNormal_IsExcludedAsync()
    {
        // Ordinary reconciliation handles a CSO that has left Pending Provisioning; this projection must
        // not resurrect it as a retry candidate.
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);
        var cso = CreatePendingProvisioningCso(system, type, attribute, "EMP0001", status: ConnectedSystemObjectStatus.Normal);
        var pendingExport = CreateExportedCreate(system, cso);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_OtherObjectType_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);

        var otherType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var otherAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = attribute.Name, ConnectedSystemObjectType = otherType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, IsExternalId = true, Selected = true
        };
        otherType.Attributes.Add(otherAttribute);
        seed.Add(otherType);
        await seed.SaveChangesAsync();

        var cso = CreatePendingProvisioningCso(system, otherType, otherAttribute, "EMP0001");
        var pendingExport = CreateExportedCreate(system, cso);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_OtherConnectedSystem_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);
        var (otherSystem, otherType, otherAttribute) = await SeedSystemAndTypeAsync(seed, systemName: "Other System");

        var cso = CreatePendingProvisioningCso(otherSystem, otherType, otherAttribute, "EMP0001");
        var pendingExport = CreateExportedCreate(otherSystem, cso);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id);

        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Partition filter

    [Test]
    public async Task GetExportedCreatePendingExportRetryCandidateSummariesAsync_PartitionFilter_OnlyMatchingPartitionReturnedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);

        var matchingPartition = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "ou=match", Name = "Match" };
        var otherPartition = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "ou=other", Name = "Other" };
        seed.AddRange(matchingPartition, otherPartition);
        await seed.SaveChangesAsync();

        var matchingCso = CreatePendingProvisioningCso(system, type, attribute, "EMP0001");
        matchingCso.Partition = matchingPartition;
        var otherCso = CreatePendingProvisioningCso(system, type, attribute, "EMP0002");
        otherCso.Partition = otherPartition;

        var matchingExport = CreateExportedCreate(system, matchingCso);
        var otherExport = CreateExportedCreate(system, otherCso);
        seed.AddRange(matchingExport, otherExport);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportedCreatePendingExportRetryCandidateSummariesAsync(system.Id, type.Id, matchingPartition.Id);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].PendingExportId, Is.EqualTo(matchingExport.Id));
    }

    #endregion
}
