// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Every statement that inserts or updates <c>ConnectedSystemObjects</c> rows either sets the synchronisation-pending
/// mark (#2092) or is listed here as exempt with its reason. Delta Synchronisation's correctness under concurrency
/// rests on the mark: a writer that changes what synchronisation must see without setting it is selected only by its
/// timestamps again, which is exactly the race #2092 closed, and nothing else would notice. This is the writer-side
/// sibling of <see cref="BulkInsertColumnCompletenessTests"/>: a new write path fails this test until someone decides,
/// in the registry below, which kind it is.
/// <para>
/// The scan is over source text, because the writers are raw SQL the EF model cannot see. It finds INSERT, UPDATE and
/// COPY statements naming the table, and EF inserts through <c>ConnectedSystemObjects.Add</c>; whole-entity EF saves
/// need no entry, since the model ignores the mark on update. Deletes need none either: a deleted row has nothing left
/// to synchronise. Each writer's behaviour is proved against PostgreSQL by <c>SynchronisationPendingDatabaseTests</c>.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemObjectWriterCompletenessTests
{
    /// <summary>The methods whose writes announce a change synchronisation must process; each must set the mark.</summary>
    private static readonly string[] MarkingWriters =
    [
        // Import create (parameterised INSERT, and the COPY writer that import and provisioning share).
        "BulkInsertConnectedSystemObjectsRawAsync",
        "BulkInsertCsosOnConnectionAsync",
        // The parallel COPY create commits rows before their values, so it marks after the values, in their transaction.
        "MarkConnectedSystemObjectsSynchronisationPendingOnConnectionAsync",
        // Provisioning by export evaluation when the save is not deferred.
        "CreateConnectedSystemObjectAsync",
        // Import update, Full Import deletion detection and explicit Delta Import deletes.
        "BulkUpdateConnectedSystemObjectsRawAsync",
        // Schema Refresh Removal.
        "ObsoleteConnectedSystemObjectsByIdsAsync",
        // A Unique Value Generation commit loser, whose own data is unchanged but must be processed again (#2309).
        "MarkConnectedSystemObjectsSynchronisationPendingAsync"
    ];

    /// <summary>Methods that write the table without announcing a change, with the reason each needs no mark.</summary>
    private static readonly Dictionary<string, string> ExemptWriters = new()
    {
        ["BulkUpdateConnectedSystemObjectJoinStatesRawAsync"] =
            "synchronisation's own join-state write; it clears the mark (guarded) rather than setting it",
        ["ClearConnectedSystemObjectMarksAsync"] = "the guarded clear itself",
        ["MarkConnectedSystemObjectsDerivedInputChangePendingAsync"] = "the derived-input mark (#1750), its own selection arm",
        ["ClearConnectedSystemObjectScopeReviewPendingAsync"] = "synchronisation clearing the Temporal Scope Reconciler flag it processed",
        ["MarkConnectedSystemObjectsScopeEvaluatedAsync"] = "the Temporal Scope Reconciler's flag (#892), its own selection reason",
        ["StampImportStateAsync"] = "import bookkeeping (SPEC-1082) after the values it describes, which their writer marked, committed",
        ["UpdateConnectedSystemObjectsWithNewAttributeValuesAsync"] =
            "nulls the import-state hash beside an export confirmation's values; the confirming import marks the object",
        ["NullImportStateForCsosAsync"] = "nulls the import-state hash; no data synchronisation reads changes",
        ["TryClaimConnectedSystemObjectForJoinAsync"] = "export matching joining the object: a synchronisation decision, not a data change",
        ["DisconnectConnectedSystemObjectsAsync"] = "synchronisation's own disconnection",
        ["DeleteMetaverseObjectsAsync"] = "releases the join of objects whose Metaverse Object is deleted: a synchronisation outcome",
        ["DeleteMetaverseObjectAsync"] = "releases the join of objects whose Metaverse Object is deleted: a synchronisation outcome",
        ["DeleteAllConnectedSystemObjectsAndDependenciesAsync"] = "releases joins of rows deleted by the same operation"
    };

    private static readonly Regex WriteStatement = new(
        @"(?:\b(?:UPDATE|INSERT\s+INTO|COPY)\s+""{1,2}ConnectedSystemObjects""{1,2}(?:[\s(]|$))|(?:\bConnectedSystemObjects\.(?:Add|AddRange)\()",
        RegexOptions.Compiled);

    private static readonly Regex MethodDeclaration = new(
        @"^\s*(?:public|private|internal|protected)\b[^=;]*?\b(\w+)\s*(?:<[^()]*>)?\(",
        RegexOptions.Compiled);

    private sealed record Writer(string Method, string File, int Line, string Body);

    private static List<Writer> FindWriters()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Join(root.FullName, "JIM.sln")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null, "repository root (JIM.sln) not found above the test directory");

        var writers = new List<Writer>();
        var sourceRoot = Path.Join(root!.FullName, "src", "JIM.PostgresData");
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
        {
            var lines = File.ReadAllLines(file);
            var declarations = lines
                .Select((text, index) => (Match: MethodDeclaration.Match(text), Index: index))
                .Where(d => d.Match.Success)
                .ToList();

            for (var i = 0; i < lines.Length; i++)
            {
                if (!WriteStatement.IsMatch(lines[i]) || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;

                var start = declarations.LastOrDefault(d => d.Index <= i);
                Assert.That(start.Match, Is.Not.Null, $"no enclosing method found for the write at {file}:{i + 1}");
                var end = declarations.FirstOrDefault(d => d.Index > i);
                var bodyEnd = end.Match == null ? lines.Length : end.Index;
                var body = string.Join("\n", lines[start.Index..bodyEnd]);
                writers.Add(new Writer(start.Match!.Groups[1].Value, Path.GetFileName(file), i + 1, body));
            }
        }

        return writers;
    }

    [Test]
    public void EveryConnectedSystemObjectWriter_IsClassifiedAsMarkingOrExempt()
    {
        var unclassified = FindWriters()
            .Where(w => !MarkingWriters.Contains(w.Method) && !ExemptWriters.ContainsKey(w.Method))
            .Select(w => $"{w.Method} ({w.File}:{w.Line})")
            .Distinct()
            .ToList();

        Assert.That(unclassified, Is.Empty,
            "A new write to ConnectedSystemObjects must either set SynchronisationPending in the same statement or " +
            "transaction as its change (and be added to MarkingWriters, with a SynchronisationPendingDatabaseTests " +
            "test), or be added to ExemptWriters with the reason it announces no change synchronisation must process: " +
            string.Join(", ", unclassified));
    }

    [Test]
    public void EveryMarkingWriter_SetsTheMark()
    {
        var writers = FindWriters();
        using (Assert.EnterMultipleScope())
        {
            foreach (var method in MarkingWriters)
            {
                var found = writers.Where(w => w.Method == method).ToList();
                Assert.That(found, Is.Not.Empty, $"{method} is listed as a marking writer but no longer writes the table; remove it");
                foreach (var writer in found)
                {
                    Assert.That(writer.Body, Does.Contain("SynchronisationPending").IgnoreCase,
                        $"{method} ({writer.File}:{writer.Line}) writes ConnectedSystemObjects without setting SynchronisationPending");
                }
            }
        }
    }

    [Test]
    public void EveryExemptWriter_StillWritesTheTable()
    {
        var methods = FindWriters().Select(w => w.Method).ToHashSet();
        var stale = ExemptWriters.Keys.Where(m => !methods.Contains(m)).ToList();
        Assert.That(stale, Is.Empty, "exempt entries that no longer write ConnectedSystemObjects; remove them: " + string.Join(", ", stale));
    }
}
