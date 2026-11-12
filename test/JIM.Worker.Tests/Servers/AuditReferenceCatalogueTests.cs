// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Pins, against the live schema, how audit rows refer to Connected System Objects and Metaverse Objects (#2669), and to
/// the attributes they record changes to (#2931).
/// <para>
/// An audit row (a change record, a change value, a Run Profile Execution Item, an Activity) names the object it is
/// about by a plain id: no foreign key, written once and never rewritten, resolved when it is read (live object, then
/// the object's deletion record, then the row's own snapshot). A foreign key there is what made every deletion rewrite
/// rows in JIM's largest tables, by <c>ON DELETE SET NULL</c> or by a statement run first to satisfy <c>NO ACTION</c>,
/// and the rewrite destroyed the reference it claimed to keep. A foreign key is also what installs a referential
/// trigger on the referenced table, so the exact set of foreign keys into the two object tables doubles as the guard
/// against a trigger coming back.
/// </para>
/// <para>
/// Live data (an object's own values, joins, references between objects, Pending Exports and other queued work) keeps
/// its constraints and is still cleared on deletion; each such foreign key is listed below with the reason it exists.
/// A new one fails here until it is added with its reason, or turned into a plain id if it is history.
/// </para>
/// <para>
/// A change record's attribute rows name their attribute the same way: by a plain id, beside the name and data type
/// copied when the row was written. A foreign key there made deleting an attribute rewrite every history row naming
/// it, discarding the id, and made every history write check and lock the attribute's row. Live data referring to an
/// attribute (values, configuration, generated values) cascades with it or refuses its deletion; only history would
/// keep a row once its attribute is gone, which is what <c>ON DELETE SET NULL</c> is for, so no foreign key into an
/// attribute table may have it.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AuditReferenceCatalogueTests
{
    /// <summary>
    /// One audit column that names an object by id.
    /// </summary>
    /// <param name="Table">The audit table.</param>
    /// <param name="Column">The column holding the object's or attribute's id.</param>
    /// <param name="ObjectTable">The table the id names a row of.</param>
    /// <param name="Index">
    /// The index readers depend on to find the rows naming one object (an object's history, its timeline, its deletion
    /// record), or null where nothing reads by the column.
    /// </param>
    public sealed record AuditReference(string Table, string Column, string ObjectTable, string? Index);

    /// <summary>
    /// Every audit column naming a Connected System Object, a Metaverse Object or an attribute.
    /// </summary>
    private static readonly AuditReference[] AuditReferences =
    [
        new("ConnectedSystemObjectChanges", "ConnectedSystemObjectId", "ConnectedSystemObjects",
            "IX_ConnectedSystemObjectChanges_ConnectedSystemObjectId"),
        // Nothing reads change values by the object they name, so the column carries no index (one index write fewer
        // per change value inserted).
        new("ConnectedSystemObjectChangeAttributeValues", "ReferenceValueId", "ConnectedSystemObjects", null),
        new("ActivityRunProfileExecutionItems", "ConnectedSystemObjectId", "ConnectedSystemObjects",
            "IX_ActivityRunProfileExecutionItems_ConnectedSystemObjectId"),
        new("MetaverseObjectChanges", "MetaverseObjectId", "MetaverseObjects", "IX_MetaverseObjectChanges_MetaverseObjectId"),
        new("MetaverseObjectChangeAttributeValues", "ReferenceValueId", "MetaverseObjects", null),
        new("Activities", "MetaverseObjectId", "MetaverseObjects", "IX_Activities_MetaverseObjectId"),
        // The attribute a change record's attribute row records (#2931). The Metaverse Object change search finds the
        // changes to one attribute by this column; the Connected System side keeps its index to match, until a
        // measurement shows it unused.
        new("ConnectedSystemObjectChangeAttributes", "AttributeId", "ConnectedSystemAttributes",
            "IX_ConnectedSystemObjectChangeAttributes_AttributeId"),
        new("MetaverseObjectChangeAttributes", "AttributeId", "MetaverseAttributes", "IX_MetaverseObjectChangeAttributes_AttributeId")
    ];

    /// <summary>The tables holding attribute definitions.</summary>
    private static readonly string[] AttributeTables = ["ConnectedSystemAttributes", "MetaverseAttributes"];

    /// <summary>
    /// Every foreign key into <c>ConnectedSystemObjects</c> and <c>MetaverseObjects</c>, keyed by the referencing table
    /// and column, with why it is a constraint: each is live data, which the deletion paths clear or remove with the
    /// object, never history.
    /// </summary>
    private static readonly Dictionary<string, string> LiveDataForeignKeys = new()
    {
        ["ConnectedSystemObjectAttributeValues.ConnectedSystemObjectId -> ConnectedSystemObjects"] =
            "An object's own values, deleted with it by cascade.",
        ["ConnectedSystemObjectAttributeValues.ReferenceValueId -> ConnectedSystemObjects"] =
            "A live reference between objects (a group's member); the deletion clears it before the delete.",
        ["DeferredReferences.SourceCsoId -> ConnectedSystemObjects"] =
            "Queued reference work for the object, deleted with it by cascade.",
        ["GeneratedValueAssignments.ConnectedSystemObjectId -> ConnectedSystemObjects"] =
            "A generated value the object holds, retired and then deleted with it by cascade.",
        ["MetaverseObjectAttributeValues.UnresolvedReferenceValueId -> ConnectedSystemObjects"] =
            "A Metaverse Object reference staged on the object; the deletion clears it before the delete.",
        ["PendingExchangeChanges.ConnectedSystemObjectId -> ConnectedSystemObjects"] =
            "Queued Exchange work for the object, cleared by the database.",
        ["PendingExports.ConnectedSystemObjectId -> ConnectedSystemObjects"] =
            "A Pending Export for the object, cleared by the database.",
        ["PendingPasswordChanges.ConnectedSystemObjectId -> ConnectedSystemObjects"] =
            "A queued password change for the object, cleared by the database.",
        ["PendingPasswordChanges.ProvisionedConnectedSystemObjectId -> ConnectedSystemObjects"] =
            "A queued password change for a provisioned object, cleared by the database.",
        ["ConnectedSystemObjects.MetaverseObjectId -> MetaverseObjects"] =
            "The join; both Metaverse Object deleters detach it before the delete.",
        ["DeferredReferences.TargetMvoId -> MetaverseObjects"] =
            "Queued reference work naming the object, deleted with it by cascade.",
        ["GeneratedValueAssignments.MetaverseObjectId -> MetaverseObjects"] =
            "A generated value the object holds, retired and then deleted with it by cascade.",
        ["GeneratedValueRevisionsPending.MetaverseObjectId -> MetaverseObjects"] =
            "Queued generated value revision for the object, deleted with it by cascade.",
        ["MetaverseObjectAttributeValues.MetaverseObjectId -> MetaverseObjects"] =
            "An object's own values, deleted with it by cascade.",
        ["MetaverseObjectAttributeValues.ReferenceValueId -> MetaverseObjects"] =
            "A live reference between objects (a Manager, a member); MetaverseReferenceRowCleanup deletes or clears it.",
        ["MetaverseObjectRole.StaticMembersId -> MetaverseObjects"] =
            "Role membership, deleted with the object by cascade.",
        ["PendingExchangeChanges.MetaverseObjectId -> MetaverseObjects"] =
            "Queued Exchange work for the object, cleared by the database.",
        ["PendingExports.SourceMetaverseObjectId -> MetaverseObjects"] =
            "The object a Pending Export was staged from, cleared by the database.",
        ["PendingPasswordChanges.MetaverseObjectId -> MetaverseObjects"] =
            "A queued password change for the object, deleted with it by cascade."
    };

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL audit reference catalogue tests.");

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

    private static IEnumerable<TestCaseData> References() =>
        AuditReferences.Select(r => new TestCaseData(r).SetName($"{{m}}({r.Table}.{r.Column})"));

    [TestCaseSource(nameof(References))]
    public async Task AuditReference_HasNoForeignKeyAsync(AuditReference reference)
    {
        await using var ctx = NewContext();

        var constraints = await ctx.Database.SqlQueryRaw<string>(
            @"SELECT c.conname::text AS ""Value""
              FROM pg_constraint c
              INNER JOIN pg_class child ON child.oid = c.conrelid
              INNER JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY(c.conkey)
              WHERE c.contype = 'f' AND child.relname = {0} AND a.attname = {1}",
            reference.Table, reference.Column).ToListAsync();

        Assert.That(constraints, Is.Empty,
            $"{reference.Table}.{reference.Column} is history naming a row of {reference.ObjectTable} by id, and must not be a " +
            "foreign key: a constraint makes every deletion of that row rewrite the history that names it. Found: " +
            string.Join(", ", constraints));
    }

    [TestCaseSource(nameof(References))]
    public async Task AuditReference_DeclaredIndexIsPresentAsync(AuditReference reference)
    {
        if (reference.Index == null)
            Assert.Pass($"{reference.Table}.{reference.Column} is read by no one by its value, so it declares no index.");

        await using var ctx = NewContext();

        var leadingColumns = await ctx.Database.SqlQueryRaw<string>(
            @"SELECT a.attname::text AS ""Value""
              FROM pg_index i
              INNER JOIN pg_class index_class ON index_class.oid = i.indexrelid
              INNER JOIN pg_class table_class ON table_class.oid = i.indrelid
              INNER JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[0]
              WHERE index_class.relname = {0} AND table_class.relname = {1}",
            reference.Index!, reference.Table).ToListAsync();

        Assert.That(leadingColumns, Is.EqualTo(new[] { reference.Column }),
            $"{reference.Index} must exist on {reference.Table} and lead with {reference.Column}: it is how an object's " +
            "history, timeline and deletion record (or an attribute's changes) are found. Removing a relationship drops its convention index, so a " +
            "migration that removes one must declare the index again under its existing name.");
    }

    [Test]
    public async Task ForeignKeysIntoTheObjectTables_AreExactlyTheLiveDataSetAsync()
    {
        await using var ctx = NewContext();

        var actual = await ctx.Database.SqlQueryRaw<string>(
            @"SELECT (child.relname || '.' || a.attname || ' -> ' || parent.relname)::text AS ""Value""
              FROM pg_constraint c
              INNER JOIN pg_class child ON child.oid = c.conrelid
              INNER JOIN pg_class parent ON parent.oid = c.confrelid
              INNER JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
              WHERE c.contype = 'f' AND parent.relname IN ('ConnectedSystemObjects', 'MetaverseObjects')
              ORDER BY 1").ToListAsync();

        var unexpected = actual.Except(LiveDataForeignKeys.Keys).ToList();
        var missing = LiveDataForeignKeys.Keys.Except(actual).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unexpected, Is.Empty,
                "these foreign keys into the object tables are not listed as live data. History must name an object by a " +
                "plain id (no constraint, no navigation), or every deletion rewrites it; if the row is genuinely live data, " +
                "add it to LiveDataForeignKeys with its reason:" + Environment.NewLine + string.Join(Environment.NewLine, unexpected));
            Assert.That(missing, Is.Empty,
                "these listed foreign keys no longer exist; remove them from LiveDataForeignKeys:" + Environment.NewLine +
                string.Join(Environment.NewLine, missing));
        }
    }

    /// <summary>
    /// A foreign key into an attribute table either takes its row with the attribute (live data: values, configuration,
    /// generated values) or refuses the attribute's deletion. One that keeps its row and clears the reference is history
    /// naming the attribute, which must hold a plain id instead, or deleting the attribute rewrites every row naming it.
    /// </summary>
    [Test]
    public async Task ForeignKeysIntoTheAttributeTables_NeverKeepTheirRowWhenTheAttributeGoesAsync()
    {
        await using var ctx = NewContext();

        var keeping = await ctx.Database.SqlQueryRaw<string>(
            @"SELECT (child.relname || '.' || a.attname || ' -> ' || parent.relname || '  [' || c.conname || ']')::text AS ""Value""
              FROM pg_constraint c
              INNER JOIN pg_class child ON child.oid = c.conrelid
              INNER JOIN pg_class parent ON parent.oid = c.confrelid
              INNER JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
              WHERE c.contype = 'f' AND parent.relname = ANY(@attributeTables) AND c.confdeltype IN ('n', 'd')
              ORDER BY 1",
            new Npgsql.NpgsqlParameter("attributeTables", AttributeTables)).ToListAsync();

        Assert.That(keeping, Is.Empty,
            "these foreign keys keep their row when its attribute is deleted, which makes it history naming the attribute. " +
            "History names an attribute by a plain id beside its copied name and type (no constraint, no navigation), or " +
            "every attribute deletion rewrites it:" + Environment.NewLine + string.Join(Environment.NewLine, keeping));
    }

    /// <summary>
    /// No audit table refers to an attribute table by a foreign key of any kind: a cascade would delete history with the
    /// attribute, and a refusal would make an attribute that history names impossible to delete.
    /// </summary>
    [Test]
    public async Task AuditTables_HaveNoForeignKeyIntoTheAttributeTablesAsync()
    {
        await using var ctx = NewContext();

        var found = await ctx.Database.SqlQueryRaw<string>(
            @"SELECT (child.relname || '.' || a.attname || ' -> ' || parent.relname || '  [' || c.conname || ']')::text AS ""Value""
              FROM pg_constraint c
              INNER JOIN pg_class child ON child.oid = c.conrelid
              INNER JOIN pg_class parent ON parent.oid = c.confrelid
              INNER JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
              WHERE c.contype = 'f' AND parent.relname = ANY(@attributeTables) AND child.relname = ANY(@auditTables)
              ORDER BY 1",
            new Npgsql.NpgsqlParameter("attributeTables", AttributeTables),
            new Npgsql.NpgsqlParameter("auditTables", AuditReferences.Select(r => r.Table).Distinct().ToArray())).ToListAsync();

        Assert.That(found, Is.Empty,
            "history must name an attribute by a plain id, never by a foreign key:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    /// <summary>
    /// No foreign key into the object tables reaches an audit table any more, so a <c>TRUNCATE ... CASCADE</c> of the
    /// object tables no longer empties them. The factory reset must reach each one through a table its truncate names, or
    /// by a containment key from one, or the reset leaves history naming objects that are gone.
    /// </summary>
    [Test]
    public async Task EveryAuditTable_IsEmptiedByTheFactoryResetTruncateAsync()
    {
        var truncated = Regex.Matches(SystemRepository.TruncateAllCustomerDataSql, "\"(\\w+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.That(truncated, Is.Not.Empty, "the truncate list was read");

        await using var ctx = NewContext();

        // A truncate with CASCADE empties every table holding a foreign key into a truncated table, whatever its delete
        // rule, and so on down.
        var emptied = await ctx.Database.SqlQueryRaw<string>(
            @"WITH RECURSIVE fk AS (
                  SELECT child.relname::text COLLATE ""C"" AS child, parent.relname::text COLLATE ""C"" AS parent
                  FROM pg_constraint c
                  INNER JOIN pg_class child ON child.oid = c.conrelid
                  INNER JOIN pg_class parent ON parent.oid = c.confrelid
                  WHERE c.contype = 'f'
              ),
              emptied(tbl) AS (
                  SELECT unnest(@truncated)::text COLLATE ""C""
                  UNION
                  SELECT fk.child FROM emptied e INNER JOIN fk ON fk.parent = e.tbl
              )
              SELECT tbl::text AS ""Value"" FROM emptied",
            new Npgsql.NpgsqlParameter("truncated", truncated)).ToListAsync();

        var notEmptied = AuditReferences.Select(r => r.Table).Distinct().Except(emptied).ToList();
        Assert.That(notEmptied, Is.Empty,
            "the factory reset's truncate no longer reaches these audit tables; name each one (or a table it is contained " +
            "by) in SystemRepository.TruncateAllCustomerDataSql: " + string.Join(", ", notEmptied));
    }
}
