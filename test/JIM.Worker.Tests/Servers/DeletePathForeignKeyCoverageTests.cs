// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Schema-level guard for the raw-SQL deletion paths (issues #1477, #1533).
/// <para>
/// Two places in JIM remove a large, connected set of rows with hand-written SQL rather than by letting EF Core
/// work out the order: the factory reset, and deleting a Connected System. Both run their whole sequence in one
/// transaction, and both depend on every row that references something they delete either being deleted first or
/// having its reference severed first. Miss one and PostgreSQL refuses that statement with <c>23503</c> (or
/// <c>23001</c> for a RESTRICT), the entire transaction rolls back, and the operation cannot succeed at all
/// until the schema or the sequence changes. That is how #1477 was found, and the same gap has since appeared
/// twice more: a nested Container hierarchy, and a Connected System configured for Password Synchronisation.
/// </para>
/// <para>
/// The behavioural cover for each is in <see cref="SystemResetDatabaseTests"/> and
/// <see cref="JIM.Worker.Tests.Repositories.ConnectedSystemDeletionDatabaseTests"/>, but a behavioural test can
/// only exercise the child rows somebody thought to seed. This asserts the property directly against the live
/// schema instead, so a child table added in a future release cannot silently reintroduce the class of fault:
/// every foreign key that would block a delete root must point at a table the sequence empties, or be listed in
/// the surface's severed set with the statement that clears it.
/// </para>
/// <para>
/// <b>What this cannot see.</b> The schema knows which tables a statement could touch, not which rows it does
/// touch, so a delete whose <c>WHERE</c> clause covers only some of a table's rows still reads as total here.
/// That is exactly the shape of the Container hierarchy fault (#1477's descendant case), and it is why the
/// behavioural fixtures remain the primary cover; this fixture catches the commoner regression, where a new
/// child table appears and no statement is added for it.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DeletePathForeignKeyCoverageTests
{
    /// <summary>
    /// One hand-written deletion sequence, described in the terms the foreign-key walk needs.
    /// </summary>
    /// <param name="Name">How the surface is named in a failure message.</param>
    /// <param name="DeleteRoots">
    /// The tables the walk starts from: those the sequence deletes rows from by a statement of its own, whose
    /// dependants therefore have to be accounted for.
    /// </param>
    /// <param name="TruncatedTables">
    /// Tables the sequence empties wholesale with <c>TRUNCATE ... RESTART IDENTITY CASCADE</c> before anything
    /// else runs. A truncate cascade empties every referencing table whatever its delete rule, so the walk both
    /// stops at these and ignores everything below them: nothing down there holds a row by the time the deletes
    /// run. Empty for a sequence that truncates nothing.
    /// </param>
    /// <param name="RemovedTables">
    /// Tables whose relevant rows the sequence removes, whether by a statement of its own or by a cascade from
    /// one. A foreign key pointing at one of these cannot block, because no row is left to block with. Unlike
    /// <paramref name="TruncatedTables"/> the walk still passes through them, because their own dependants are
    /// still live rows in live tables.
    /// </param>
    /// <param name="SeveredForeignKeys">
    /// Foreign keys the sequence deliberately leaves in place and clears instead, keyed by constraint name with
    /// the statement that clears it. Anything here that stops being cleared becomes a rollback, so each entry
    /// names where to look.
    /// </param>
    public sealed record DeleteSurface(
        string Name,
        IReadOnlyList<string> DeleteRoots,
        IReadOnlyList<string> TruncatedTables,
        IReadOnlyList<string> RemovedTables,
        IReadOnlyDictionary<string, string> SeveredForeignKeys);

    /// <summary>
    /// <c>SystemRepository.ResetSystemAsync</c>: truncate the customer's data, then remove custom configuration
    /// with a set of <c>DELETE ... WHERE "BuiltIn" = false</c> statements.
    /// </summary>
    private static readonly DeleteSurface FactoryReset = new(
        Name: "the factory reset (SystemRepository.ResetSystemAsync)",
        DeleteRoots:
        [
            "PredefinedSearches",
            "ExampleDataTemplates",
            "ExampleDataSets",
            "Roles",
            "MetaverseObjectTypes",
            "MetaverseAttributes",
            "ConnectorDefinitions",
            "ApiKeys"
        ],
        TruncatedTables:
        [
            "WorkerTasks",
            "DeferredReferences",
            "PendingExports",
            "ConnectedSystemObjectChanges",
            "MetaverseObjectChanges",
            "ScheduleExecutions",
            "Activities",
            "Schedules",
            "SyncRules",
            "ObjectMatchingRules",
            "ConnectedSystemObjects",
            "ConnectedSystems",
            "MetaverseObjects",
            "TrustedCertificates"
        ],
        // Nothing beyond the truncate: the mixed deletes are keyed on "BuiltIn" = false and so remove only part
        // of each table they name, which is not the total removal this list asserts.
        RemovedTables: [],
        SeveredForeignKeys: new Dictionary<string, string>
        {
            ["FK_ServiceSettings_MetaverseAttributes_SSOUniqueIdentifierMeta~"] =
                "Cascading would delete the preserved Service Settings singleton along with a custom Metaverse " +
                "Attribute chosen as the SSO unique identifier. The wipe clears the reference explicitly instead."
        });

    /// <summary>
    /// <c>ConnectedSystemRepository.DeleteConnectedSystemAsync</c>: remove one Connected System and everything
    /// beneath it, severing the audit and history references that are deliberately kept.
    /// <para>
    /// This also covers <c>DeleteAllConnectedSystemObjectsAndDependenciesAsync</c>, which the sequence calls as
    /// its first step. That helper is reachable on its own (clearing a system's Connected System Objects without
    /// deleting the system), where it runs a strict subset of these statements against the same rows.
    /// </para>
    /// </summary>
    private static readonly DeleteSurface ConnectedSystemDeletion = new(
        Name: "deleting a Connected System (ConnectedSystemRepository.DeleteConnectedSystemAsync)",
        DeleteRoots: ["ConnectedSystems"],
        TruncatedTables: [],
        RemovedTables:
        [
            // Statements in DeleteAllConnectedSystemObjectsAndDependenciesAsync.
            "PendingExportAttributeValueChanges",
            "PendingExports",
            "DeferredReferences",
            "ConnectedSystemObjectAttributeValues",
            "ConnectedSystemObjects",
            // Statements in DeleteConnectedSystemAsync itself.
            "ConnectedSystemContainers",
            "ConnectedSystemRunProfiles",
            "ConnectedSystemPartitions",
            "SyncRuleMappingSources",
            "SyncRuleMappings",
            "SyncRuleScopingCriteria",
            "SyncRuleScopingCriteriaGroups",
            "ObjectMatchingRules",
            "SyncRules",
            "ConnectedSystemAttributes",
            "ConnectedSystemPasswordSynchronisations",
            "ConnectedSystemObjectTypes",
            "ConnectedSystemSettingValues",
            "ConnectedSystemPasswordPolicies",
            // Removed by cascade from the Object Matching Rules deleted above.
            "ObjectMatchingRuleSources"
        ],
        SeveredForeignKeys: new Dictionary<string, string>
        {
            ["FK_Activities_ConnectedSystems_ConnectedSystemId"] =
                "Activities are retained for audit; step 2a nulls the reference to the deleted system.",
            ["FK_Activities_ConnectedSystemRunProfiles_ConnectedSystemRunPro~"] =
                "Activities are retained for audit; step 2a nulls the reference to the deleted Run Profiles.",
            ["FK_Activities_SyncRules_SyncRuleId"] =
                "Activities are retained for audit; step 2a nulls the reference to the deleted Synchronisation Rules.",
            ["FK_MetaverseObjectChanges_SyncRules_SyncRuleId"] =
                "Metaverse Object change history is retained; step 2b nulls the reference to the deleted " +
                "Synchronisation Rules.",
            ["FK_MetaverseObjectAttributeValues_ConnectedSystems_Contributed~"] =
                "The contributed value is retained and only its contributor cleared; step 2c nulls it. Attribute " +
                "recall is a sync-engine concern, deliberately out of scope for bulk system deletion.",
            ["FK_MetaverseObjectAttributeValues_ConnectedSystemObjects_Unres~"] =
                "The metaverse value is retained and only the now-unresolvable staged reference cleared; step 7b " +
                "nulls it.",
            ["FK_ExampleDataTemplateAttributes_ConnectedSystemAttributes_Con~"] =
                "Example Data Templates are retained; step 2d nulls the reference to this system's schema attributes.",
            ["FK_ConnectedSystemObjectChanges_ConnectedSystemObjectTypes_Del~"] =
                "On the preserve-history path the change rows are kept; step 2e nulls DeletedObjectTypeId before " +
                "step 13 removes the Object Types. On the delete-history path the rows are already gone.",
            ["FK_ConnectedSystemObjectChanges_ConnectedSystemObjectAttribute~"] =
                "On the preserve-history path the change rows are kept; step 5 nulls " +
                "DeletedObjectExternalIdAttributeValueId before step 6 removes the attribute values. On the " +
                "delete-history path the rows are already gone."
        });

    private static IEnumerable<TestCaseData> Surfaces()
    {
        yield return new TestCaseData(FactoryReset).SetName("{m}(factory reset)");
        yield return new TestCaseData(ConnectedSystemDeletion).SetName("{m}(Connected System deletion)");
    }

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL foreign-key coverage tests.");

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

    /// <summary>
    /// Walks the foreign-key graph from each of a surface's delete roots through cascading edges only, and
    /// reports any edge it meets that would refuse the delete.
    /// <para>
    /// Only <c>CASCADE</c> and <c>SET NULL</c> are safe: the first takes the referencing row with it, the second
    /// clears the reference in place (<see cref="EverySetNullForeignKey_TargetsANullableColumnAsync"/> checks
    /// that it can). <c>NO ACTION</c>, <c>RESTRICT</c> and <c>SET DEFAULT</c> all refuse, so each has to be
    /// answered by the surface's own lists.
    /// </para>
    /// <para>
    /// The explicit <c>"C"</c> collations are required because a recursive CTE will not mix the default
    /// collation with the one <c>pg_class</c> carries.
    /// </para>
    /// </summary>
    private const string BlockingForeignKeySql = @"
        WITH RECURSIVE fk AS (
            SELECT c.conname::text COLLATE ""C"" AS conname,
                   child.relname::text COLLATE ""C"" AS child,
                   parent.relname::text COLLATE ""C"" AS parent,
                   c.confdeltype
            FROM pg_constraint c
            INNER JOIN pg_class child ON child.oid = c.conrelid
            INNER JOIN pg_class parent ON parent.oid = c.confrelid
            WHERE c.contype = 'f'
        ),
        emptied(tbl) AS (
            SELECT unnest(@truncated)::text COLLATE ""C""
            UNION
            SELECT fk.child FROM emptied e INNER JOIN fk ON fk.parent = e.tbl
        ),
        reachable(tbl, depth, path) AS (
            SELECT unnest(@roots)::text COLLATE ""C"", 0, unnest(@roots)::text COLLATE ""C""
            UNION ALL
            SELECT fk.child, r.depth + 1, (r.path || ' > ' || fk.child)::text COLLATE ""C""
            FROM reachable r INNER JOIN fk ON fk.parent = r.tbl
            WHERE r.depth < 12
              AND position(fk.child in r.path) = 0
              AND fk.confdeltype = 'c'
              AND fk.child NOT IN (SELECT tbl FROM emptied)
        )
        SELECT DISTINCT (r.path || ' -> ' || fk.child || '  [' || fk.conname || ']')::text COLLATE ""C"" AS ""Value""
        FROM reachable r INNER JOIN fk ON fk.parent = r.tbl
        WHERE fk.confdeltype NOT IN ('c', 'n')
          AND fk.child NOT IN (SELECT tbl FROM emptied)
          AND fk.child <> ALL(@removed)
          AND fk.conname <> ALL(@severed)
        ORDER BY 1;";

    [TestCaseSource(nameof(Surfaces))]
    public async Task EveryBlockingForeignKey_IsRemovedOrSeveredAsync(DeleteSurface surface)
    {
        await using var ctx = NewContext();

        var blocking = await ctx.Database.SqlQueryRaw<string>(
            BlockingForeignKeySql,
            new Npgsql.NpgsqlParameter("truncated", surface.TruncatedTables.ToArray()),
            new Npgsql.NpgsqlParameter("roots", surface.DeleteRoots.ToArray()),
            new Npgsql.NpgsqlParameter("removed", surface.RemovedTables.ToArray()),
            new Npgsql.NpgsqlParameter("severed", surface.SeveredForeignKeys.Keys.ToArray()))
            .ToListAsync();

        Assert.That(blocking, Is.Empty,
            $"each of these foreign keys will refuse {surface.Name} once the referencing row exists, rolling the " +
            "whole operation back. Delete the referencing rows in the sequence (and add the table to " +
            "RemovedTables), or sever the reference and add the constraint to SeveredForeignKeys naming the " +
            "statement that clears it:" + Environment.NewLine + string.Join(Environment.NewLine, blocking));
    }

    /// <summary>
    /// Guards each surface's severed set: an entry naming a constraint that no longer exists is stale, and would
    /// hide a genuinely blocking foreign key if the name were ever reused.
    /// </summary>
    [TestCaseSource(nameof(Surfaces))]
    public async Task SeveredForeignKeys_AllStillExistAsync(DeleteSurface surface)
    {
        await using var ctx = NewContext();

        var existing = await ctx.Database.SqlQueryRaw<string>(
            @"SELECT conname::text AS ""Value"" FROM pg_constraint WHERE contype = 'f';").ToListAsync();

        var stale = surface.SeveredForeignKeys.Keys.Where(name => !existing.Contains(name)).ToList();
        Assert.That(stale, Is.Empty,
            $"{surface.Name} lists these severed foreign keys, and the schema no longer has them: " +
            string.Join(", ", stale));
    }

    /// <summary>
    /// The walk above treats <c>SET NULL</c> as safe, which holds only while the column it nulls actually accepts
    /// null. PostgreSQL allows the pairing and fails at delete time, so this asserts the premise rather than
    /// assuming it.
    /// </summary>
    [Test]
    public async Task EverySetNullForeignKey_TargetsANullableColumnAsync()
    {
        await using var ctx = NewContext();

        var offenders = await ctx.Database.SqlQueryRaw<string>(
            @"SELECT (child.relname || '.' || a.attname || '  [' || c.conname || ']')::text AS ""Value""
              FROM pg_constraint c
              INNER JOIN pg_class child ON child.oid = c.conrelid
              CROSS JOIN LATERAL unnest(c.conkey) AS k(attnum)
              INNER JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
              WHERE c.contype = 'f' AND c.confdeltype = 'n' AND a.attnotnull
              ORDER BY 1;").ToListAsync();

        Assert.That(offenders, Is.Empty,
            "a SET NULL foreign key on a NOT NULL column refuses the parent delete at run time rather than " +
            "clearing the reference, so the deletion-path walk would treat a blocking edge as safe:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
