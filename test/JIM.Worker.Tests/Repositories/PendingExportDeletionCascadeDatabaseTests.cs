// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification for issue #1818: <c>ConnectedSystemRepository.DeletePendingExportAsync</c>
/// used to delete the Pending Export via EF <c>Remove()</c> + <c>SaveChangesAsync()</c>. Because the
/// <see cref="PendingExport"/>-<see cref="PendingExportAttributeValueChange"/> relationship carried no
/// explicit <c>OnDelete</c> configuration, EF fell back to its default <c>ClientSetNull</c> cascade: one
/// <c>UPDATE</c> per tracked child nulling <c>PendingExportId</c>, and the child rows themselves were
/// never removed. Orphaned <c>PendingExportAttributeValueChanges</c> rows accumulated forever (123,184
/// rows after one 100k-object test run; 104,460 per-row <c>UPDATE</c>s). The fix routes the singular
/// delete through the batch path (<c>DeletePendingExportsAsync</c>), which already deletes children then
/// parent via raw SQL, and configures the database foreign key itself to cascade as a second, independent
/// safety net for any other deletion path.
/// <para>
/// Seeding pattern mirrors <see cref="ExportMatchCandidateDatabaseTests"/>: everything a test writes is
/// created and saved through ONE seed context, never split across separately-disposed contexts (EF's
/// <c>Add()</c>/<c>AddRange()</c> walks the whole reachable entity graph and re-inserts every untracked
/// navigation it finds; src/CLAUDE.md &gt; "DbSet.Add Walks the Graph").
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportDeletionCascadeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export deletion cascade tests.");

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
    /// Seeds a Connected System, one Object Type with one attribute, and a Connected System Object, all on
    /// the caller's own context (see the class remarks: everything must share one seed context).
    /// </summary>
    private static async Task<(ConnectedSystem System, ConnectedSystemObject Cso, ConnectedSystemObjectTypeAttribute Attribute)> SeedConnectedSystemAsync(JimDbContext seed)
    {
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid()}", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Deletion Cascade Target", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId",
            ConnectedSystemObjectType = type,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        type.Attributes.Add(attribute);

        var cso = new ConnectedSystemObject
        {
            Type = type,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined
        };

        seed.AddRange(connectorDefinition, system, type, cso);
        await seed.SaveChangesAsync();

        return (system, cso, attribute);
    }

    private static PendingExport CreatePendingExportWithChanges(
        ConnectedSystem system, ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, int changeCount)
    {
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = system,
            ConnectedSystemId = system.Id,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        for (var i = 0; i < changeCount; i++)
        {
            pe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                Attribute = attribute,
                AttributeId = attribute.Id,
                StringValue = $"value-{i}",
                ChangeType = PendingExportAttributeChangeType.Update
            });
        }

        return pe;
    }

    [Test]
    public async Task DeletePendingExportAsync_TrackedFromFreshContext_DeletesAllChildRowsWithNoOrphansAsync()
    {
        await using var seed = NewContext();
        var (system, cso, attribute) = await SeedConnectedSystemAsync(seed);
        var pe = CreatePendingExportWithChanges(system, cso, attribute, changeCount: 3);
        seed.Add(pe);
        await seed.SaveChangesAsync();

        var childIds = pe.AttributeValueChanges.Select(avc => avc.Id).ToList();

        // Production loads the Pending Export tracked, with AttributeValueChanges included, before deleting
        // it (ExportEvaluationServer.EnsureDeletePendingExportAsync via
        // GetPendingExportLightweightByConnectedSystemObjectIdAsync).
        await using var ctx = NewContext();
        var tracked = await ctx.PendingExports
            .Include(p => p.AttributeValueChanges)
            .SingleAsync(p => p.Id == pe.Id);
        var repository = new PostgresDataRepository(ctx);

        await repository.ConnectedSystems.DeletePendingExportAsync(tracked);

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.PendingExports.AnyAsync(p => p.Id == pe.Id), Is.False,
                "the Pending Export itself must be gone");
            Assert.That(await verify.PendingExportAttributeValueChanges.CountAsync(avc => childIds.Contains(avc.Id)), Is.EqualTo(0),
                "every attribute value change belonging to the deleted Pending Export must be gone, not merely orphaned");
            Assert.That(await verify.PendingExportAttributeValueChanges.CountAsync(avc => avc.PendingExportId == null), Is.EqualTo(0),
                "deleting a Pending Export must never leave orphaned attribute value change rows (#1818)");
        }
    }

    [Test]
    public async Task DeletePendingExportAsync_TrackedWithChanges_IssuesNoPerRowStatementsAsync()
    {
        await using var seed = NewContext();
        var (system, cso, attribute) = await SeedConnectedSystemAsync(seed);
        var pe = CreatePendingExportWithChanges(system, cso, attribute, changeCount: 5);
        seed.Add(pe);
        await seed.SaveChangesAsync();

        var interceptor = new CommandTextCapturingInterceptor();
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(interceptor)
            .Options;
        await using var ctx = new JimDbContext(options);
        var tracked = await ctx.PendingExports
            .Include(p => p.AttributeValueChanges)
            .SingleAsync(p => p.Id == pe.Id);
        var repository = new PostgresDataRepository(ctx);
        interceptor.CommandTexts.Clear();

        await repository.ConnectedSystems.DeletePendingExportAsync(tracked);

        // An EF Remove() + SaveChangesAsync() touches the children one row at a time (an UPDATE per change
        // under the old ClientSetNull behaviour, a DELETE per change under a client-side cascade); at group
        // scale that is thousands of statements per delete. The set-based delete keys on the parent instead.
        var perRowStatements = interceptor.CommandTexts
            .Where(text => text.Contains("\"PendingExportAttributeValueChanges\"") && text.Contains("WHERE \"Id\" ="))
            .ToList();
        Assert.That(perRowStatements, Is.Empty,
            "deleting a Pending Export must remove its attribute value changes with one set-based statement, not one per row");
    }

    [Test]
    public async Task PendingExportAttributeValueChanges_ForeignKey_CascadesFromPendingExportDeletionAsync()
    {
        await using var seed = NewContext();
        var (system, cso, attribute) = await SeedConnectedSystemAsync(seed);
        var pe = CreatePendingExportWithChanges(system, cso, attribute, changeCount: 2);
        seed.Add(pe);
        await seed.SaveChangesAsync();

        var childIds = pe.AttributeValueChanges.Select(avc => avc.Id).ToList();

        // Bypass the application entirely: a raw SQL delete of the parent row must be enough on its own,
        // proving the database's own foreign key cascades rather than relying solely on application code
        // to order its deletes correctly (#1818).
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""PendingExports"" WHERE ""Id"" = {pe.Id}");

        await using var verify = NewContext();
        Assert.That(await verify.PendingExportAttributeValueChanges.CountAsync(avc => childIds.Contains(avc.Id)), Is.EqualTo(0),
            "the database's own foreign key must cascade-delete attribute value changes when their Pending Export is removed");
    }

    /// <summary>
    /// Records the text of every command the provider executes, so a test can assert on statement shape.
    /// </summary>
    private sealed class CommandTextCapturingInterceptor : DbCommandInterceptor
    {
        public List<string> CommandTexts { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
