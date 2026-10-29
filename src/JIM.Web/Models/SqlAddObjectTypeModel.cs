// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// The step logic of the SQL Connector's Add Object Type dialog (#2570): the Object Type being built, which step
/// it is on, which steps must be completed before moving on, the anchor JIM preselects from a primary key (and why
/// it preselected nothing), and the references it suggests from foreign keys. Kept apart from the dialog so the
/// rules can be tested without rendering it.
/// </summary>
/// <remarks>
/// The Source and Anchor steps must be completed, because an Object Type without them is one the Connector
/// refuses. References and multi-valued attributes are optional, so the Object Type can be added from the Anchor
/// step onwards.
/// </remarks>
public sealed class SqlAddObjectTypeModel
{
    private readonly IReadOnlyList<SqlObjectTypeDocument> _existingObjectTypes;
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);

    /// <summary>The anchor JIM last preselected, so it can tell an anchor it chose from one the administrator chose.</summary>
    private List<string> _preselectedAnchor = [];
    private SqlAnchorOrigin _readOrigin = SqlAnchorOrigin.None;

    /// <param name="existingObjectTypes">The Object Types already in the document, which the new one's foreign keys may point at.</param>
    /// <param name="schema">A schema to start with, such as that of a table a foreign key points at.</param>
    /// <param name="table">A table to start with.</param>
    public SqlAddObjectTypeModel(IEnumerable<SqlObjectTypeDocument> existingObjectTypes, string? schema = null, string? table = null)
    {
        _existingObjectTypes = [.. existingObjectTypes];
        ObjectType = new SqlObjectTypeDocument { Schema = schema, Table = table };
    }

    /// <summary>The Object Type being built.</summary>
    public SqlObjectTypeDocument ObjectType { get; }

    /// <summary>The step the dialog is on.</summary>
    public SqlAddObjectTypeStep Step { get; private set; } = SqlAddObjectTypeStep.Source;

    /// <summary>
    /// Whether the objects come from a SELECT statement rather than a table or view. The document holds exactly
    /// one of the two, so switching clears the other.
    /// </summary>
    public bool ReadsFromStatement
    {
        get => ObjectType.Select != null;
        set
        {
            if (value == ReadsFromStatement)
                return;

            if (value)
            {
                ObjectType.Select = string.Empty;
                ObjectType.Schema = null;
                ObjectType.Table = null;
            }
            else
            {
                ObjectType.Select = null;
            }
        }
    }

    /// <summary>The source's columns and keys, once read.</summary>
    public ConnectorCatalogueObjectDetail? Source { get; private set; }

    /// <summary>Why the source's columns could not be read, where they could not.</summary>
    public string? SourceFailure { get; private set; }

    /// <summary>Where the anchor came from, or why JIM preselected nothing.</summary>
    public SqlAnchorOrigin AnchorOrigin
    {
        get
        {
            var anchor = AnchorColumns;
            if (anchor.Count > 0 && !anchor.SequenceEqual(_preselectedAnchor, StringComparer.OrdinalIgnoreCase))
                return SqlAnchorOrigin.Chosen;

            return _readOrigin;
        }
    }

    /// <summary>Whether the current step is complete enough to move on from.</summary>
    public bool CanContinue => Step switch
    {
        SqlAddObjectTypeStep.Source => IsSourceComplete,
        SqlAddObjectTypeStep.Anchor => IsSourceComplete && AnchorColumns.Count > 0,
        _ => true
    };

    /// <summary>Whether the Object Type can be added now: from the Anchor step onwards, once it has an anchor.</summary>
    public bool CanFinish => Step != SqlAddObjectTypeStep.Source && IsSourceComplete && AnchorColumns.Count > 0;

    /// <summary>
    /// The references the source's foreign keys suggest, by the same rules as the cards. A key can point at the
    /// Object Type being added, as a manager does, so it counts as one of the document's. Offers to add yet another
    /// Object Type are left out: that would be a dialog within a dialog, and the card offers it once this one is added.
    /// </summary>
    public IReadOnlyList<SqlObjectTypeSuggestion> ReferenceSuggestions =>
        [.. SqlObjectTypesEditorModel.Suggest(ObjectType, [.. _existingObjectTypes, ObjectType], Source, _dismissed.Contains)
            .Where(s => s.Kind == SqlObjectTypeSuggestionKind.Reference)];

    private bool IsSourceComplete =>
        !string.IsNullOrWhiteSpace(ObjectType.Name) &&
        (ReadsFromStatement ? !string.IsNullOrWhiteSpace(ObjectType.Select) : !string.IsNullOrWhiteSpace(ObjectType.Table));

    private List<string> AnchorColumns => (ObjectType.AnchorColumns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList();

    /// <summary>
    /// Takes the source's columns and keys, once read. Where the source declares a primary key, it becomes the
    /// anchor, in key order, unless the administrator has chosen one; an anchor JIM preselected from an earlier
    /// source is replaced, since it belonged to that source.
    /// </summary>
    /// <param name="source">The columns and keys; null where they could not be read.</param>
    /// <param name="failure">Why they could not be read, where they could not.</param>
    public void SourceRead(ConnectorCatalogueObjectDetail? source, string? failure)
    {
        var chosen = AnchorOrigin == SqlAnchorOrigin.Chosen;
        Source = source;
        SourceFailure = failure;

        if (source == null)
        {
            _readOrigin = SqlAnchorOrigin.ColumnsUnreadable;
            return;
        }

        if (source.PrimaryKey.Count == 0)
        {
            _readOrigin = source.Kind switch
            {
                ConnectorCatalogueObjectKind.View => SqlAnchorOrigin.ViewDeclaresNoKeys,
                ConnectorCatalogueObjectKind.Statement => SqlAnchorOrigin.StatementDeclaresNoKeys,
                _ => SqlAnchorOrigin.TableHasNoPrimaryKey
            };

            if (!chosen)
            {
                ObjectType.AnchorColumns = null;
                _preselectedAnchor = [];
            }

            return;
        }

        _readOrigin = SqlAnchorOrigin.PrimaryKey;
        if (!chosen)
        {
            _preselectedAnchor = [.. source.PrimaryKey];
            ObjectType.AnchorColumns = [.. source.PrimaryKey];
        }
    }

    /// <summary>Moves to the next step, when the current one is complete.</summary>
    public void Next()
    {
        if (CanContinue && Step < SqlAddObjectTypeStep.MultiValuedAttributes)
            Step++;
    }

    /// <summary>Moves to the previous step.</summary>
    public void Back()
    {
        if (Step > SqlAddObjectTypeStep.Source)
            Step--;
    }

    /// <summary>Configures a suggested column as a reference.</summary>
    public void AcceptReference(SqlObjectTypeSuggestion suggestion) => SqlObjectTypesEditorModel.ApplyReference(ObjectType, suggestion);

    /// <summary>Stops offering a suggestion while the dialog is open.</summary>
    public void Dismiss(SqlObjectTypeSuggestion suggestion) => _dismissed.Add(suggestion.ConstraintName);

    /// <summary>
    /// The finished Object Type, as a hand-written one would be: rows left empty are dropped, and empty lists are
    /// left out rather than written as <c>[]</c>.
    /// </summary>
    public SqlObjectTypeDocument Build()
    {
        var anchor = AnchorColumns;
        var columns = (ObjectType.Columns ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        var relatedTables = (ObjectType.RelatedTables ?? []).ToList();

        return new SqlObjectTypeDocument
        {
            Name = ObjectType.Name?.Trim(),
            Schema = ReadsFromStatement || string.IsNullOrWhiteSpace(ObjectType.Schema) ? null : ObjectType.Schema,
            Table = ReadsFromStatement ? null : ObjectType.Table,
            Select = ReadsFromStatement ? ObjectType.Select : null,
            AnchorColumns = anchor.Count == 0 ? null : [.. anchor.Select(c => (string?)c)],
            Columns = columns.Count == 0 ? null : columns,
            RelatedTables = relatedTables.Count == 0 ? null : relatedTables
        };
    }
}
