// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Support;

/// <summary>
/// Reads every synchronisation history table, and the Activities and stat counters, and renders them as text that does
/// not depend on ids or times (#2874): every id is replaced by a natural label (an object by the name the test gives it,
/// a history row by its place in the history), every timestamp is dropped, and every set of rows is sorted. Two runs
/// that recorded the same history render identically, whichever ids and times they were given and in whatever order
/// their rows were written.
/// </summary>
/// <remarks>
/// Every column is read (<c>SELECT *</c>), so a column added to a history table later is characterised without
/// changing this. A row's own id is replaced by its label; a column naming another row (an item, an outcome, a change
/// record, a Pending Export, an object) shows that row's label; an id this cannot label shows as <c>?</c> followed by
/// the column, and the number of those heads the output, so an unlabelled id is visible rather than silently equal.
/// </remarks>
internal static partial class SynchronisationHistorySnapshot
{
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?")]
    private static partial Regex TimestampPattern();

    /// <summary>
    /// Columns left out of every table: a row's own id (shown as its label instead) and values that differ between two
    /// runs recording the same history.
    /// </summary>
    private static readonly HashSet<string> IgnoredColumns = new(StringComparer.Ordinal)
    {
        "Id",
        "xmin",
        // The Activity's progress message carries throughput, which is timing.
        "Message"
    };

    /// <summary>
    /// Renders the history of the database the connection string names.
    /// </summary>
    /// <param name="connectionString">The database.</param>
    /// <param name="objectLabels">Labels for the ids of Connected System Objects and Metaverse Objects, live or deleted.</param>
    /// <param name="activityStatuses">The status each Activity would be given on completion, by Activity id; shown with
    /// the Activity, since a processor run alone leaves it in progress.</param>
    public static async Task<string> CaptureAsync(
        string connectionString,
        IReadOnlyDictionary<Guid, string> objectLabels,
        IReadOnlyDictionary<Guid, string> activityStatuses)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var activities = await ReadAsync(connection, "Activities");
        var pendingExports = await ReadAsync(connection, "PendingExports");
        var items = await ReadAsync(connection, "ActivityRunProfileExecutionItems");
        var outcomes = await ReadAsync(connection, "ActivityRunProfileExecutionItemSyncOutcomes");
        var candidates = await ReadAsync(connection, "ActivityRunProfileExecutionItemMatchCandidates");
        var edges = await ReadAsync(connection, "CausalEdges");
        var csoChanges = await ReadAsync(connection, "ConnectedSystemObjectChanges");
        var csoChangeAttributes = await ReadAsync(connection, "ConnectedSystemObjectChangeAttributes");
        var csoChangeValues = await ReadAsync(connection, "ConnectedSystemObjectChangeAttributeValues");
        var mvoChanges = await ReadAsync(connection, "MetaverseObjectChanges");
        var mvoChangeAttributes = await ReadAsync(connection, "MetaverseObjectChangeAttributes");
        var mvoChangeValues = await ReadAsync(connection, "MetaverseObjectChangeAttributeValues");
        var counters = await ReadAsync(connection, "ActivityStatCounters");

        var labels = new Dictionary<Guid, string>(objectLabels);

        // Activities in the order they were created, which is the order the test ran them.
        var orderedActivities = activities.OrderBy(a => Convert.ToDateTime(a["Created"], CultureInfo.InvariantCulture)).ToList();
        for (var n = 0; n < orderedActivities.Count; n++)
            labels[(Guid)orderedActivities[n]["Id"]!] = $"Activity{n + 1}";

        // Pending Exports by the object they change and how; one that a later evaluation replaced is gone, and anything
        // naming it shows as unlabelled.
        foreach (var pendingExport in pendingExports)
            labels[(Guid)pendingExport["Id"]!] = $"PendingExport({Label(labels, pendingExport["ConnectedSystemObjectId"])},{pendingExport["ChangeType"]})";

        // Items by their Activity, object, change type and snapshot name.
        AssignUniqueLabels(items, labels, item =>
            $"Item({Label(labels, item["ActivityId"])}|{Label(labels, item["ConnectedSystemObjectId"] ?? item["MetaverseObjectId"])}|" +
            $"{item["ObjectChangeType"]}|{item["DisplayNameSnapshot"]}|{item["ErrorType"]})");

        // Outcomes by their item and their path down the item's outcome tree.
        var outcomesById = outcomes.ToDictionary(o => (Guid)o["Id"]!);
        string OutcomeLabel(Dictionary<string, object?> outcome)
        {
            var id = (Guid)outcome["Id"]!;
            if (labels.TryGetValue(id, out var existing))
                return existing;
            var step = $"{outcome["OutcomeType"]}#{outcome["Ordinal"]}";
            var label = outcome["ParentSyncOutcomeId"] is Guid parentId && outcomesById.TryGetValue(parentId, out var parent)
                ? $"{OutcomeLabel(parent)}/{step}"
                : $"{Label(labels, outcome["ActivityRunProfileExecutionItemId"])}/{step}";
            labels[id] = label;
            return label;
        }

        foreach (var outcome in outcomes)
            OutcomeLabel(outcome);
        AssertUnique(outcomes.Select(o => labels[(Guid)o["Id"]!]), "outcome");

        // Change records by the outcome or item they belong to.
        var outcomeByCsoChangeId = outcomes
            .Where(o => o["ConnectedSystemObjectChangeId"] is Guid)
            .GroupBy(o => (Guid)o["ConnectedSystemObjectChangeId"]!)
            .ToDictionary(g => g.Key, g => g.Select(o => labels[(Guid)o["Id"]!]).OrderBy(l => l, StringComparer.Ordinal).First());
        AssignUniqueLabels(csoChanges, labels, change =>
            outcomeByCsoChangeId.TryGetValue((Guid)change["Id"]!, out var outcomeLabel)
                ? $"CsoChange(of {outcomeLabel})"
                : $"CsoChange({Label(labels, change["ActivityRunProfileExecutionItemId"])}|{change["ChangeType"]}|" +
                  $"{Label(labels, change["ConnectedSystemObjectId"] ?? change["DeletedConnectedSystemObjectId"])})");
        AssignUniqueLabels(mvoChanges, labels, change =>
            $"MvoChange({Label(labels, change["ActivityRunProfileExecutionItemId"])}|{change["ChangeType"]}|" +
            $"{Label(labels, change["MetaverseObjectId"] ?? change["DeletedMetaverseObjectId"])}|{Label(labels, change["ActivityId"])})");

        var output = new StringBuilder();
        var unlabelled = 0;

        string Render(Dictionary<string, object?> row, params string[] alsoIgnored)
        {
            var parts = new List<string>();
            foreach (var (column, value) in row.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (IgnoredColumns.Contains(column) || alsoIgnored.Contains(column))
                    continue;
                if (value is DateTime or DateTimeOffset or TimeSpan)
                    continue;

                string text;
                switch (value)
                {
                    case null:
                        text = "null";
                        break;
                    case Guid guid:
                        if (labels.TryGetValue(guid, out var label))
                            text = label;
                        else
                        {
                            unlabelled++;
                            text = $"?{column}";
                        }
                        break;
                    case string s when column == "ErrorStackTrace":
                        // Line numbers move with every edit; whether there was one is the history.
                        text = s.Length > 0 ? "<stack trace>" : "";
                        break;
                    case string s:
                        text = TimestampPattern().Replace(GuidPattern().Replace(s, m =>
                        {
                            if (labels.TryGetValue(Guid.Parse(m.Value), out var embedded))
                                return embedded;
                            unlabelled++;
                            return "?guid";
                        }), "<time>");
                        break;
                    case IFormattable formattable:
                        text = formattable.ToString(null, CultureInfo.InvariantCulture);
                        break;
                    default:
                        text = value.ToString() ?? "";
                        break;
                }

                parts.Add($"{column}={text}");
            }

            return string.Join("; ", parts);
        }

        void Section(string title, IEnumerable<string> lines)
        {
            output.Append("## ").AppendLine(title);
            foreach (var line in lines.OrderBy(l => l, StringComparer.Ordinal))
                output.AppendLine(line);
            output.AppendLine();
        }

        string Self(Dictionary<string, object?> row) => labels[(Guid)row["Id"]!];

        Section("Activities", orderedActivities.Select(a =>
            $"{Self(a)}: CompletionStatus={activityStatuses.GetValueOrDefault((Guid)a["Id"]!, "not completed")}; {Render(a, "Status")}"));
        Section("Execution items", items.Select(i => $"{Self(i)}: {Render(i)}"));
        Section("Outcomes", outcomes.Select(o => $"{Self(o)}: {Render(o)}"));
        Section("Match candidates", candidates.Select(c => Render(c)));
        Section("Causal edges", edges.Select(e => Render(e)));
        Section("Connected System Object changes", RenderChangeTree(csoChanges, csoChangeAttributes, csoChangeValues,
            "ConnectedSystemChangeId", "ConnectedSystemObjectChangeAttributeId", Self, Render));
        Section("Metaverse Object changes", RenderChangeTree(mvoChanges, mvoChangeAttributes, mvoChangeValues,
            "MetaverseObjectChangeId", "MetaverseObjectChangeAttributeId", Self, Render));
        Section("Stat counters", counters.Select(c => Render(c)));

        return $"Unlabelled ids: {unlabelled}{Environment.NewLine}{Environment.NewLine}{output}";
    }

    /// <summary>
    /// Each change record, then its attribute rows, each followed by its value rows, every level sorted, so the record
    /// renders the same whichever ids its rows were given.
    /// </summary>
    private static IEnumerable<string> RenderChangeTree(
        List<Dictionary<string, object?>> changes,
        List<Dictionary<string, object?>> attributes,
        List<Dictionary<string, object?>> values,
        string attributeParentColumn,
        string valueParentColumn,
        Func<Dictionary<string, object?>, string> self,
        Func<Dictionary<string, object?>, string[], string> render)
    {
        var attributesByChange = attributes.ToLookup(a => a[attributeParentColumn]);
        var valuesByAttribute = values.ToLookup(v => v[valueParentColumn]);

        foreach (var change in changes)
        {
            var block = new StringBuilder();
            block.Append(self(change)).Append(": ").Append(render(change, []));
            var renderedAttributes = attributesByChange[change["Id"]]
                .Select(attribute =>
                {
                    var renderedValues = valuesByAttribute[attribute["Id"]]
                        .Select(v => "    value: " + render(v, [valueParentColumn]))
                        .OrderBy(v => v, StringComparer.Ordinal);
                    return string.Join(Environment.NewLine,
                        new[] { "  attribute: " + render(attribute, [attributeParentColumn]) }.Concat(renderedValues));
                })
                .OrderBy(a => a, StringComparer.Ordinal);
            foreach (var attribute in renderedAttributes)
                block.AppendLine().Append(attribute);
            yield return block.ToString();
        }
    }

    private static string Label(Dictionary<Guid, string> labels, object? id) => id switch
    {
        null => "none",
        Guid guid when labels.TryGetValue(guid, out var label) => label,
        Guid => "?",
        _ => id.ToString() ?? "none"
    };

    private static void AssignUniqueLabels(
        List<Dictionary<string, object?>> rows,
        Dictionary<Guid, string> labels,
        Func<Dictionary<string, object?>, string> labelOf)
    {
        var assigned = rows.Select(r => (Id: (Guid)r["Id"]!, Label: labelOf(r))).ToList();
        AssertUnique(assigned.Select(a => a.Label), "row");
        foreach (var (id, label) in assigned)
            labels[id] = label;
    }

    private static void AssertUnique(IEnumerable<string> labels, string kind)
    {
        var duplicates = labels.GroupBy(l => l, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.That(duplicates, Is.Empty,
            $"two history rows of one kind ({kind}) share a natural label, so the snapshot cannot tell them apart; give the label more of the row");
    }

    private static async Task<List<Dictionary<string, object?>>> ReadAsync(NpgsqlConnection connection, string table)
    {
        // The table name is fixed in code, never anything a caller supplies.
        await using var command = new NpgsqlCommand($"""SELECT * FROM "{table}" """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }
}
