// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Connectors.Sql;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// What the SQL Connector's Object Types editor edits: the Object Types document itself, plus the rules the
/// editor applies around it (what a collapsed card summarises, which problems belong to which card, which foreign
/// keys to suggest as references, and what renaming or removing an Object Type does to the references others hold
/// to it). Kept apart from the components so those rules can be tested without rendering anything.
/// </summary>
/// <remarks>
/// The model edits <see cref="SqlObjectTypesDocument"/>, the same class the Connector's parser reads, so there is
/// no second representation of the document to drift from the first. Problems come from the parser's own
/// validation; the editor adds only the Delta Import Mode rules, which depend on settings the document does not hold.
/// </remarks>
public sealed class SqlObjectTypesEditorModel
{
    private readonly SqlObjectTypesDocument _document;
    private readonly HashSet<(SqlObjectTypeDocument ObjectType, string ConstraintName)> _dismissed = [];
    private IReadOnlyList<SqlObjectTypesProblem> _problems = [];

    private SqlObjectTypesEditorModel(SqlObjectTypesDocument document)
    {
        _document = document;
        Revalidate();
    }

    /// <summary>The Object Types, in document order.</summary>
    public List<SqlObjectTypeDocument> ObjectTypes => _document.ObjectTypes ??= [];

    /// <summary>
    /// The Connected System's Delta Import Mode as it stands on screen, or null where none is chosen. Decides
    /// whether a watermark column or a change log is a problem when missing.
    /// </summary>
    public string? DeltaImportMode { get; set; }

    /// <summary>
    /// The Object Types selected for synchronisation on the Schema tab. Only these need what the Delta Import Mode
    /// reads, which is the rule saving the Schema tab applies; null where the schema has not been imported.
    /// </summary>
    public ISet<string>? SelectedObjectTypeNames { get; set; }

    /// <summary>Problems that concern no one Object Type, such as a document with no Object Types at all.</summary>
    public IReadOnlyList<string> DocumentProblems => [.. _problems.Where(p => p.ObjectTypeIndex == null).Select(p => p.Message)];

    /// <summary>
    /// Reads a stored document into a model. A document with problems loads, so they can be fixed in the editor;
    /// one that is not JSON, or uses a field the document does not have, does not, because the editor could only
    /// show it by dropping what it cannot represent.
    /// </summary>
    /// <param name="json">The setting's stored value; empty or null for a Connected System with no Object Types yet.</param>
    /// <param name="model">The loaded model, or null when it could not be loaded.</param>
    /// <param name="failure">Why it could not be loaded, in the parser's words.</param>
    public static bool TryLoad(string? json, out SqlObjectTypesEditorModel? model, out string? failure)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            model = new SqlObjectTypesEditorModel(new SqlObjectTypesDocument { ObjectTypes = [] });
            failure = null;
            return true;
        }

        try
        {
            model = new SqlObjectTypesEditorModel(SqlObjectTypesDocument.FromJson(json));
            failure = null;
            return true;
        }
        catch (JsonException)
        {
            // The parser's own account is better than the serialiser's: it names the field and suggests the
            // intended one. Every JsonException here is also one of its problems, located the same way.
            model = null;
            failure = SqlObjectTypesDocument.Validate(json).FirstOrDefault()?.Message ?? "The document is not valid JSON.";
            return false;
        }
    }

    /// <summary>The document in its canonical layout, as the setting stores it.</summary>
    public string ToJson() => _document.ToJson();

    /// <summary>Re-reads the parser's problems after a change.</summary>
    public void Revalidate() => _problems = SqlObjectTypesDocument.Validate(ToJson());

    /// <summary>
    /// The problems on one Object Type's card: the parser's, then what the Delta Import Mode needs and it lacks.
    /// </summary>
    public IReadOnlyList<string> ProblemsFor(int index)
    {
        var problems = _problems.Where(p => p.ObjectTypeIndex == index).Select(p => p.Message).ToList();
        if (index < 0 || index >= ObjectTypes.Count)
            return problems;

        var objectType = ObjectTypes[index];
        if (objectType.Name == null || SelectedObjectTypeNames?.Contains(objectType.Name) != true)
            return problems;

        if (DeltaImportMode == SqlConnectorConstants.DeltaImportModeWatermarkColumn)
        {
            if (string.IsNullOrWhiteSpace(objectType.WatermarkColumn))
                problems.Add($"Delta Import Mode is '{SqlConnectorConstants.DeltaImportModeWatermarkColumn}' and this Object Type is selected for synchronisation, so it needs a watermark column. Choose one, or deselect the Object Type on the Schema tab if JIM never imports it.");

            problems.AddRange((objectType.RelatedTables ?? [])
                .Where(relatedTable => string.IsNullOrWhiteSpace(relatedTable.WatermarkColumn))
                .Select(relatedTable => $"Delta Import Mode is '{SqlConnectorConstants.DeltaImportModeWatermarkColumn}', so multi-valued attribute '{relatedTable.AttributeName}' needs a watermark column on its own table: a row added to or removed from it changes the object without touching the object's own row."));
        }
        else if (DeltaImportMode == SqlConnectorConstants.DeltaImportModeChangeLogTable && objectType.ChangeLog == null)
        {
            problems.Add($"Delta Import Mode is '{SqlConnectorConstants.DeltaImportModeChangeLogTable}' and this Object Type is selected for synchronisation, so it needs a change-log table. Choose one, or deselect the Object Type on the Schema tab if JIM never imports it.");
        }

        return problems;
    }

    /// <summary>
    /// Where an Object Type's objects come from, for its card's heading.
    /// </summary>
    public static string SourceOf(SqlObjectTypeDocument objectType)
    {
        if (!string.IsNullOrWhiteSpace(objectType.Select))
            return "SELECT statement";

        if (string.IsNullOrWhiteSpace(objectType.Table))
            return "No source chosen";

        return string.IsNullOrWhiteSpace(objectType.Schema) ? objectType.Table : $"{objectType.Schema}.{objectType.Table}";
    }

    /// <summary>
    /// What a collapsed card shows: its anchor, then each reference, then each multi-valued attribute.
    /// </summary>
    public static IReadOnlyList<SqlObjectTypeSummaryChip> SummaryOf(SqlObjectTypeDocument objectType)
    {
        var chips = new List<SqlObjectTypeSummaryChip>();

        var anchor = (objectType.AnchorColumns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (anchor.Count > 0)
            chips.Add(new SqlObjectTypeSummaryChip(SqlObjectTypeSummaryChipKind.Anchor, $"Anchor {string.Join(", ", anchor)}"));

        chips.AddRange((objectType.Columns ?? [])
            .Where(column => !string.IsNullOrWhiteSpace(column.Name) && !string.IsNullOrWhiteSpace(column.ReferencesObjectType))
            .Select(column => new SqlObjectTypeSummaryChip(SqlObjectTypeSummaryChipKind.Reference, $"{column.Name}: {column.ReferencesObjectType}")));

        chips.AddRange((objectType.RelatedTables ?? [])
            .Where(relatedTable => !string.IsNullOrWhiteSpace(relatedTable.AttributeName))
            .Select(relatedTable => new SqlObjectTypeSummaryChip(SqlObjectTypeSummaryChipKind.MultiValued,
                string.IsNullOrWhiteSpace(relatedTable.ReferencesObjectType)
                    ? relatedTable.AttributeName!
                    : $"{relatedTable.AttributeName}: {relatedTable.ReferencesObjectType}")));

        return chips;
    }

    /// <summary>
    /// What the foreign keys declared on an Object Type's source suggest, as schema discovery reads them: a
    /// single-column key pointing at another Object Type's single-column anchor is a reference to that Object
    /// Type; one pointing at a table no Object Type reads from offers an Object Type for it. Columns already
    /// configured as references, anchor columns and dismissed suggestions are left out.
    /// </summary>
    /// <param name="index">The Object Type whose source <paramref name="source"/> describes.</param>
    /// <param name="source">The source's columns and keys, or null where they could not be read.</param>
    public IReadOnlyList<SqlObjectTypeSuggestion> SuggestionsFor(int index, ConnectorCatalogueObjectDetail? source)
    {
        if (source == null || index < 0 || index >= ObjectTypes.Count)
            return [];

        var objectType = ObjectTypes[index];
        var references = (objectType.Columns ?? [])
            .Where(column => !string.IsNullOrWhiteSpace(column.ReferencesObjectType) && column.Name != null)
            .Select(column => column.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchor = (objectType.AnchorColumns ?? []).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var suggestions = new List<SqlObjectTypeSuggestion>();
        var offeredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = source.ForeignKeys.Where(fk =>
            fk.Columns.Count == 1 &&
            fk.ReferencedColumns.Count == 1 &&
            !references.Contains(fk.Columns[0]) &&
            !anchor.Contains(fk.Columns[0]) &&
            !_dismissed.Contains((objectType, fk.ConstraintName)));

        foreach (var foreignKey in candidates)
        {
            var readers = ObjectTypes
                .Where(other => string.IsNullOrWhiteSpace(other.Select) && ReadsFrom(other, foreignKey.ReferencedSchemaName, foreignKey.ReferencedObjectName))
                .ToList();

            if (readers.Count == 0)
            {
                if (offeredTables.Add($"{foreignKey.ReferencedSchemaName}.{foreignKey.ReferencedObjectName}"))
                    suggestions.Add(Suggest(SqlObjectTypeSuggestionKind.AddObjectType, foreignKey, null));
                continue;
            }

            var target = readers.FirstOrDefault(other =>
                !string.IsNullOrWhiteSpace(other.Name) &&
                other.AnchorColumns is [var anchorColumn] &&
                string.Equals(anchorColumn, foreignKey.ReferencedColumns[0], StringComparison.OrdinalIgnoreCase));

            if (target != null)
                suggestions.Add(Suggest(SqlObjectTypeSuggestionKind.Reference, foreignKey, target.Name));
        }

        return suggestions;
    }

    /// <summary>Stops offering a suggestion for the rest of this editing session. The document is unchanged.</summary>
    public void Dismiss(int index, SqlObjectTypeSuggestion suggestion)
    {
        if (index >= 0 && index < ObjectTypes.Count)
            _dismissed.Add((ObjectTypes[index], suggestion.ConstraintName));
    }

    /// <summary>Configures the suggested column as a reference to the suggested Object Type.</summary>
    public void AcceptReference(int index, SqlObjectTypeSuggestion suggestion)
    {
        if (suggestion.Kind != SqlObjectTypeSuggestionKind.Reference || index < 0 || index >= ObjectTypes.Count)
            return;

        var columns = ObjectTypes[index].Columns ??= [];
        var existing = columns.FirstOrDefault(c => string.Equals(c.Name, suggestion.ColumnName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            existing.ReferencesObjectType = suggestion.ReferencedObjectTypeName;
        else
            columns.Add(new SqlColumnDocument { Name = suggestion.ColumnName, ReferencesObjectType = suggestion.ReferencedObjectTypeName });

        Revalidate();
    }

    /// <summary>
    /// The primary key to offer as the anchor, in key order: only for an Object Type with no anchor yet, since
    /// the editor never second-guesses one the administrator chose. Empty for a view or query, which declare none.
    /// </summary>
    public static IReadOnlyList<string> PrimaryKeyAnchor(SqlObjectTypeDocument objectType, ConnectorCatalogueObjectDetail? source)
    {
        if (source == null || (objectType.AnchorColumns ?? []).Any(c => !string.IsNullOrWhiteSpace(c)))
            return [];

        return source.PrimaryKey;
    }

    /// <summary>Adds an Object Type at the end of the document, returning its position.</summary>
    public int AddObjectType(string? schema = null, string? table = null, IEnumerable<string>? anchorColumns = null)
    {
        ObjectTypes.Add(new SqlObjectTypeDocument
        {
            Schema = schema,
            Table = table,
            AnchorColumns = anchorColumns?.Select(c => (string?)c).ToList()
        });

        Revalidate();
        return ObjectTypes.Count - 1;
    }

    /// <summary>
    /// Renames an Object Type, moving every reference to it onto the new name, since a reference is held by name.
    /// References are left alone where the old or new name is shared with another Object Type, because it is then
    /// not clear which one they mean.
    /// </summary>
    public void RenameObjectType(int index, string? name)
    {
        if (index < 0 || index >= ObjectTypes.Count)
            return;

        var objectType = ObjectTypes[index];
        var oldName = objectType.Name;
        var newName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        objectType.Name = newName;

        var othersHold = (string? candidate) => candidate != null &&
            ObjectTypes.Any(other => !ReferenceEquals(other, objectType) && string.Equals(other.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(oldName) && newName != null && !othersHold(oldName) && !othersHold(newName))
        {
            foreach (var column in ObjectTypes.SelectMany(o => o.Columns ?? []).Where(c => string.Equals(c.ReferencesObjectType, oldName, StringComparison.OrdinalIgnoreCase)))
                column.ReferencesObjectType = newName;

            foreach (var relatedTable in ObjectTypes.SelectMany(o => o.RelatedTables ?? []).Where(r => string.Equals(r.ReferencesObjectType, oldName, StringComparison.OrdinalIgnoreCase)))
                relatedTable.ReferencesObjectType = newName;
        }

        Revalidate();
    }

    /// <summary>
    /// The references other Object Types hold to this one, described for a confirmation before removing it.
    /// Its references to itself go with it, so they are not listed.
    /// </summary>
    public IReadOnlyList<SqlObjectTypeReference> ReferencesTo(int index)
    {
        if (index < 0 || index >= ObjectTypes.Count || string.IsNullOrWhiteSpace(ObjectTypes[index].Name))
            return [];

        var target = ObjectTypes[index];
        var references = new List<SqlObjectTypeReference>();

        foreach (var other in ObjectTypes.Where(o => !ReferenceEquals(o, target)))
        {
            references.AddRange((other.Columns ?? [])
                .Where(c => string.Equals(c.ReferencesObjectType, target.Name, StringComparison.OrdinalIgnoreCase))
                .Select(c => new SqlObjectTypeReference(other.Name, c.Name, false)));

            references.AddRange((other.RelatedTables ?? [])
                .Where(r => string.Equals(r.ReferencesObjectType, target.Name, StringComparison.OrdinalIgnoreCase))
                .Select(r => new SqlObjectTypeReference(other.Name, r.AttributeName, true)));
        }

        return references;
    }

    /// <summary>
    /// Whether an Object Type has nothing configured at all, as one just added does. Removing it loses nothing,
    /// so the editor removes it without asking.
    /// </summary>
    public static bool IsBlank(SqlObjectTypeDocument objectType) =>
        string.IsNullOrWhiteSpace(objectType.Name) &&
        string.IsNullOrWhiteSpace(objectType.Schema) &&
        string.IsNullOrWhiteSpace(objectType.Table) &&
        string.IsNullOrWhiteSpace(objectType.Select) &&
        string.IsNullOrWhiteSpace(objectType.WatermarkColumn) &&
        (objectType.AnchorColumns ?? []).All(string.IsNullOrWhiteSpace) &&
        (objectType.Columns ?? []).Count == 0 &&
        (objectType.RelatedTables ?? []).Count == 0 &&
        objectType.ChangeLog == null;

    /// <summary>
    /// Removes an Object Type and the references other Object Types held to it. A referencing column goes back to
    /// being an ordinary column, and a related table stays a multi-valued attribute of plain values.
    /// </summary>
    public void RemoveObjectType(int index)
    {
        if (index < 0 || index >= ObjectTypes.Count)
            return;

        var removed = ObjectTypes[index];
        ObjectTypes.RemoveAt(index);

        if (!string.IsNullOrWhiteSpace(removed.Name) &&
            !ObjectTypes.Any(o => string.Equals(o.Name, removed.Name, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var other in ObjectTypes)
            {
                // A column configured only to say what it references has nothing left to say without it.
                other.Columns?.RemoveAll(c => string.Equals(c.ReferencesObjectType, removed.Name, StringComparison.OrdinalIgnoreCase));
                if (other.Columns is { Count: 0 })
                    other.Columns = null;

                foreach (var relatedTable in (other.RelatedTables ?? []).Where(r => string.Equals(r.ReferencesObjectType, removed.Name, StringComparison.OrdinalIgnoreCase)))
                    relatedTable.ReferencesObjectType = null;
            }
        }

        Revalidate();
    }

    /// <summary>
    /// How many of the source's columns the card does not mention, all of which schema import discovers and
    /// types without any configuration; null where the columns could not be read.
    /// </summary>
    public static int? OtherColumnCount(SqlObjectTypeDocument objectType, ConnectorCatalogueObjectDetail? source)
    {
        if (source == null)
            return null;

        var mentioned = (objectType.AnchorColumns ?? [])
            .Concat((objectType.Columns ?? []).Select(c => c.Name))
            .Append(objectType.WatermarkColumn)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return source.Columns.Count(c => !mentioned.Contains(c.Name));
    }

    /// <summary>
    /// Whether an Object Type reads from the named table: by name, ignoring case as schema discovery does, and in
    /// any schema when the Object Type names none.
    /// </summary>
    private static bool ReadsFrom(SqlObjectTypeDocument objectType, string? schemaName, string objectName) =>
        string.Equals(objectType.Table, objectName, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(objectType.Schema) || string.Equals(objectType.Schema, schemaName, StringComparison.OrdinalIgnoreCase));

    private static SqlObjectTypeSuggestion Suggest(SqlObjectTypeSuggestionKind kind, ConnectorCatalogueForeignKey foreignKey, string? objectTypeName) => new(
        kind,
        foreignKey.ConstraintName,
        foreignKey.Columns[0],
        foreignKey.ReferencedSchemaName,
        foreignKey.ReferencedObjectName,
        foreignKey.ReferencedColumns[0],
        objectTypeName);
}
