// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Enums;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JIM.Connectors.Sql;

/// <summary>
/// The Object Types document: which object types this Connected System has, where each one's objects
/// come from, and what identifies them.
/// <para>
/// The shape is richer than the flat settings framework expresses, so it is supplied as JSON in one
/// Text setting and parsed here. Parsing is strict on purpose. An unknown field is refused rather than
/// ignored, because the likeliest mistake in a hand-written document is a misspelled field name, and
/// ignoring one would leave the Connected System configured differently from how the document reads.
/// Nothing is ever partially applied: either the whole document is usable or none of it is.
/// </para>
/// <para>
/// Everything in the document is privileged administrator input, but that is not a reason to relax:
/// identifiers are validated here and quoted by the provider before they reach any command text, and
/// values are always bound as parameters (see <see cref="ISqlProvider"/>'s security contract).
/// </para>
/// </summary>
internal sealed record SqlSchemaConfiguration
{
    /// <summary>
    /// How the document is read, by the parser and by <see cref="SqlObjectTypesDocument.FromJson"/> alike.
    /// </summary>
    internal static readonly JsonSerializerOptions ParsingOptions = new()
    {
        PropertyNameCaseInsensitive = true,

        // A field name nobody recognises is a typo, and a typo that parses is a defect that only
        // surfaces as missing data much later.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    internal IReadOnlyList<SqlObjectTypeConfiguration> ObjectTypes { get; init; } = [];

    /// <summary>
    /// Reads an Object Types document.
    /// </summary>
    /// <exception cref="SqlSchemaConfigurationException">The document is missing, is not valid JSON, or says something the Connector cannot act on. The message names the object type and field at fault.</exception>
    internal static SqlSchemaConfiguration Parse(string? document)
    {
        var faults = new List<Fault>();
        var configuration = Read(document, faults);

        // The first fault is the one the parser has always reported, in the order it has always found them,
        // so every existing message (and every caller relying on one) is unchanged.
        if (faults.Count > 0)
            ExceptionDispatchInfo.Throw(faults[0].Exception);

        return configuration!;
    }

    /// <summary>
    /// Everything wrong with an Object Types document, in document order, each located as precisely as it
    /// can be; empty when <see cref="Parse"/> would accept the document.
    /// </summary>
    internal static IReadOnlyList<SqlObjectTypesProblem> Validate(string? document)
    {
        var faults = new List<Fault>();
        Read(document, faults);
        return [.. faults.Select(fault => fault.Problem)];
    }

    /// <summary>
    /// Reads the document, recording every fault rather than stopping at the first. A fault in one Object Type
    /// is recorded against it and reading moves on to the next, because the Object Types are independent of
    /// each other until their references are checked; a fault that stops the document being read at all (it
    /// is empty, or not JSON) is the only fault recorded.
    /// </summary>
    /// <returns>The configuration, or null when there is any fault.</returns>
    private static SqlSchemaConfiguration? Read(string? document, List<Fault> faults)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            AddFault(faults, new SqlSchemaConfigurationException(
                $"{SqlConnectorConstants.SettingObjectTypes} is empty. A Connected System needs at least one object type before JIM can discover its schema; see the setting's description for an example to start from."));
            return null;
        }

        SqlObjectTypesDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SqlObjectTypesDocument>(document, ParsingOptions);
        }
        catch (JsonException ex)
        {
            faults.Add(DescribeJsonFault(document, ex));
            return null;
        }

        if (parsed?.ObjectTypes == null || parsed.ObjectTypes.Count == 0)
        {
            AddFault(faults, new SqlSchemaConfigurationException($"{SqlConnectorConstants.SettingObjectTypes} must declare at least one object type in its 'objectTypes' list."));
            return null;
        }

        var objectTypes = new List<(SqlObjectTypeConfiguration ObjectType, int Index)>(parsed.ObjectTypes.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < parsed.ObjectTypes.Count; index++)
        {
            var objectTypeDocument = parsed.ObjectTypes[index];
            SqlObjectTypeConfiguration objectType;
            try
            {
                objectType = ReadObjectType(objectTypeDocument, index);
            }
            catch (SqlSchemaConfigurationException ex)
            {
                AddFault(faults, ex, index, objectTypeDocument?.Name?.Trim());
                continue;
            }

            if (!names.Add(objectType.Name))
            {
                AddFault(faults, new SqlSchemaConfigurationException($"Object Type '{objectType.Name}' is declared more than once. Object type names identify them to JIM, so each must be unique.") { Field = "name" },
                    index, objectType.Name);
                continue;
            }

            objectTypes.Add((objectType, index));
        }

        // References are cross-checked only once every name is known, so that an object type may
        // reference one declared after it (a manager reference on the first object type is the usual
        // case, and ordering a document around that would be an odd thing to ask for). Every declared
        // name counts, including one whose Object Type has a fault of its own: that fault is already
        // reported, and calling the reference undeclared as well would send the administrator looking
        // for a problem that is not there.
        var declaredNames = new HashSet<string>(
            parsed.ObjectTypes.Where(objectType => !string.IsNullOrWhiteSpace(objectType?.Name)).Select(objectType => objectType!.Name!.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (objectType, index) in objectTypes)
        {
            try
            {
                ValidateReferences(objectType, declaredNames);
            }
            catch (SqlSchemaConfigurationException ex)
            {
                AddFault(faults, ex, index, objectType.Name);
            }
        }

        return faults.Count == 0
            ? new SqlSchemaConfiguration { ObjectTypes = [.. objectTypes.Select(objectType => objectType.ObjectType)] }
            : null;
    }

    private static void AddFault(List<Fault> faults, SqlSchemaConfigurationException exception, int? objectTypeIndex = null, string? objectTypeName = null) =>
        faults.Add(new Fault(new SqlObjectTypesProblem(exception.Message, objectTypeIndex, objectTypeName, exception.Field), exception));

    /// <summary>
    /// A problem as an editor sees it, paired with the exception <see cref="Parse"/> throws for it.
    /// </summary>
    private sealed record Fault(SqlObjectTypesProblem Problem, SqlSchemaConfigurationException Exception);

    #region JSON faults

    /// <summary>
    /// The names of the fields each level of the document may have, in the camel case the document is written
    /// in, read from the model itself so a field added there is known here without anyone remembering to.
    /// </summary>
    private static readonly IReadOnlyDictionary<DocumentLevel, string[]> FieldNames = new Dictionary<DocumentLevel, string[]>
    {
        [DocumentLevel.Document] = FieldNamesOf<SqlObjectTypesDocument>(),
        [DocumentLevel.ObjectType] = FieldNamesOf<SqlObjectTypeDocument>(),
        [DocumentLevel.Column] = FieldNamesOf<SqlColumnDocument>(),
        [DocumentLevel.RelatedTable] = FieldNamesOf<SqlRelatedTableDocument>(),
        [DocumentLevel.ChangeLog] = FieldNamesOf<SqlChangeLogDocument>()
    };

    private static string[] FieldNamesOf<T>() =>
        [.. typeof(T).GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))];

    // $.objectTypes[0].columns[1].name, $.objectTypes[0].changeLog.table, $.objectTypes[0].name, $.name;
    // System.Text.Json writes a name it cannot put after a dot as ['name'].
    private static readonly Regex JsonPathPattern = new(
        @"^\$(?:\.objectTypes\[(?<index>\d+)\](?:\.(?<child>columns|relatedTables)\[\d+\]|\.(?<child>changeLog))?)?(?:\.(?<field>[^.\[\]']+)|\['(?<field>[^']*)'\])$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Turns the serialiser's account of a document it could not read into one an administrator can act on.
    /// A field the document does not have is named, with the field it was most likely meant to be, and the
    /// serialiser's own message (which names .NET types) is kept for everything else.
    /// </summary>
    private static Fault DescribeJsonFault(string document, JsonException ex)
    {
        var lineNumber = ex.LineNumber + 1;
        var column = ex.BytePositionInLine + 1;
        var match = ex.Path == null ? Match.Empty : JsonPathPattern.Match(ex.Path);

        int? objectTypeIndex = match.Groups["index"].Success ? int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) : null;
        var field = match.Groups["field"].Success ? match.Groups["field"].Value : null;
        var level = !match.Success ? DocumentLevel.Document
            : match.Groups["child"].Success ? match.Groups["child"].Value.ToLowerInvariant() switch
            {
                "columns" => DocumentLevel.Column,
                "relatedtables" => DocumentLevel.RelatedTable,
                _ => DocumentLevel.ChangeLog
            }
            : objectTypeIndex.HasValue ? DocumentLevel.ObjectType : DocumentLevel.Document;

        var objectTypeName = objectTypeIndex.HasValue ? ReadObjectTypeName(document, objectTypeIndex.Value) : null;

        string message;
        if (match.Success && field != null && !FieldNames[level].Contains(field, StringComparer.OrdinalIgnoreCase))
        {
            var suggestion = ClosestFieldName(field, FieldNames[level]);
            message = $"{DescribeLevel(level, objectTypeIndex, objectTypeName)} has an unrecognised field '{field}'." +
                      (suggestion == null ? "" : $" Did you mean '{suggestion}'?") +
                      " Fields that are not recognised are refused rather than ignored, because ignoring one would leave the Connected System configured differently from how the document reads.";
        }
        else
        {
            message = $"{SqlConnectorConstants.SettingObjectTypes} could not be read as JSON: {ex.Message}";
            field = null;
        }

        var exception = new SqlSchemaConfigurationException(message, ex) { Field = field };
        return new Fault(new SqlObjectTypesProblem(message, objectTypeIndex, objectTypeName, field, lineNumber, column), exception);
    }

    private static string DescribeLevel(DocumentLevel level, int? objectTypeIndex, string? objectTypeName)
    {
        var objectType = objectTypeName != null ? $"Object Type '{objectTypeName}'" : $"the object type in position {objectTypeIndex + 1}";
        return level switch
        {
            DocumentLevel.ObjectType => char.ToUpperInvariant(objectType[0]) + objectType[1..],
            DocumentLevel.Column => $"A configured column of {objectType}",
            DocumentLevel.RelatedTable => $"A related table of {objectType}",
            DocumentLevel.ChangeLog => $"The change log of {objectType}",
            _ => $"The {SqlConnectorConstants.SettingObjectTypes} document"
        };
    }

    /// <summary>
    /// The name an Object Type was given, read loosely so it can be quoted in the account of a fault that
    /// stopped the document being read strictly; null where the document is not JSON at all or gave none.
    /// </summary>
    private static string? ReadObjectTypeName(string document, int objectTypeIndex)
    {
        try
        {
            var objectTypes = JsonNode.Parse(document)?["objectTypes"] as JsonArray;
            return objectTypes != null && objectTypeIndex < objectTypes.Count && objectTypes[objectTypeIndex] is JsonObject objectType &&
                   objectType["name"] is JsonValue name && name.TryGetValue<string>(out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The known field an unrecognised one was most likely meant to be: within two edits of it, ignoring case;
    /// null where none is that close, because a guess further off would mislead more than it helps.
    /// </summary>
    private static string? ClosestFieldName(string field, IEnumerable<string> knownFieldNames) =>
        knownFieldNames
            .Select(known => (Name: known, Distance: EditDistance(field.ToLowerInvariant(), known.ToLowerInvariant())))
            .Where(candidate => candidate.Distance <= 2)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private enum DocumentLevel
    {
        Document,
        ObjectType,
        Column,
        RelatedTable,
        ChangeLog
    }

    #endregion

    private static SqlObjectTypeConfiguration ReadObjectType(SqlObjectTypeDocument? document, int index)
    {
        if (document == null)
            throw new SqlSchemaConfigurationException($"The object type in position {index + 1} of 'objectTypes' is empty.");

        // With no name to quote, the position in the list is the only way to point at it.
        if (string.IsNullOrWhiteSpace(document.Name))
            throw new SqlSchemaConfigurationException($"The object type in position {index + 1} of 'objectTypes' has no 'name'. Every object type needs one, because it is what JIM calls it.") { Field = "name" };

        var name = document.Name.Trim();
        var hasTable = !string.IsNullOrWhiteSpace(document.Table);
        var hasSelect = !string.IsNullOrWhiteSpace(document.Select);

        if (hasTable == hasSelect)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{name}' must name exactly one source: either a 'table' (a table or a view) or a 'select' statement, and not both.") { Field = "table" };

        if (hasSelect && !string.IsNullOrWhiteSpace(document.Schema))
            throw new SqlSchemaConfigurationException(
                $"Object Type '{name}' supplies both a 'schema' and a 'select' statement. A schema qualifies a table name; a statement qualifies its own tables, so remove the 'schema'.");

        if (hasTable)
        {
            ValidateIdentifier(document.Table, name, "table");
            if (!string.IsNullOrWhiteSpace(document.Schema))
                ValidateIdentifier(document.Schema, name, "schema");
        }
        else
        {
            ValidateSelectStatement(document.Select!, name);
        }

        var anchorColumns = ReadAnchorColumns(document, name);

        if (!string.IsNullOrWhiteSpace(document.WatermarkColumn))
            ValidateIdentifier(document.WatermarkColumn, name, "watermarkColumn");

        return new SqlObjectTypeConfiguration
        {
            Name = name,
            SchemaName = string.IsNullOrWhiteSpace(document.Schema) ? null : document.Schema,
            TableName = hasTable ? document.Table : null,
            SelectStatement = hasSelect ? document.Select!.Trim() : null,
            AnchorColumns = anchorColumns,
            Columns = ReadColumns(document, name),
            RelatedTables = ReadRelatedTables(document, name, anchorColumns.Count),
            WatermarkColumn = string.IsNullOrWhiteSpace(document.WatermarkColumn) ? null : document.WatermarkColumn,
            ChangeLog = ReadChangeLog(document.ChangeLog, name, anchorColumns.Count)
        };
    }

    /// <summary>
    /// Reads an object type's change-log configuration. Validated whichever Delta Import mode is
    /// configured, so a document that could not work in Change-Log Table mode is refused at save time
    /// rather than on the night the mode is switched on.
    /// </summary>
    private static SqlChangeLogConfiguration? ReadChangeLog(SqlChangeLogDocument? document, string objectTypeName, int anchorColumnCount)
    {
        if (document == null)
            return null;

        ValidateIdentifier(document.Table, objectTypeName, "changeLog.table");
        ValidateIdentifier(document.SequenceColumn, objectTypeName, "changeLog.sequenceColumn");
        ValidateIdentifier(document.ChangeTypeColumn, objectTypeName, "changeLog.changeTypeColumn");

        if (!string.IsNullOrWhiteSpace(document.Schema))
            ValidateIdentifier(document.Schema, objectTypeName, "changeLog.schema");

        if (document.AnchorColumns == null || document.AnchorColumns.Count != anchorColumnCount)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{objectTypeName}' identifies its change-log rows by {document.AnchorColumns?.Count ?? 0} column(s), but the object type's anchor has {anchorColumnCount}. " +
                "A change attributed by part of an anchor belongs to some other object, so 'changeLog.anchorColumns' must name one column per anchor column, in the same order.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchorColumn in document.AnchorColumns)
        {
            ValidateIdentifier(anchorColumn, objectTypeName, "changeLog.anchorColumns");

            if (!seen.Add(anchorColumn!))
                throw new SqlSchemaConfigurationException($"Object Type '{objectTypeName}' lists change-log anchor column '{anchorColumn}' more than once.");
        }

        return new SqlChangeLogConfiguration
        {
            SchemaName = string.IsNullOrWhiteSpace(document.Schema) ? null : document.Schema,
            TableName = document.Table!,
            AnchorColumns = [.. document.AnchorColumns.Select(anchorColumn => anchorColumn!)],
            SequenceColumn = document.SequenceColumn!,
            ChangeTypeColumn = document.ChangeTypeColumn!,
            ChangeTypes = ReadChangeTypes(document, objectTypeName)
        };
    }

    /// <summary>
    /// Turns the customer's own change-type vocabulary into what each value means to JIM.
    /// </summary>
    private static Dictionary<string, ObjectChangeType> ReadChangeTypes(SqlChangeLogDocument document, string objectTypeName)
    {
        var changeTypes = new Dictionary<string, ObjectChangeType>(StringComparer.OrdinalIgnoreCase);

        AddChangeTypeValues(changeTypes, document.CreateValues, ObjectChangeType.Added, objectTypeName, "createValues");
        AddChangeTypeValues(changeTypes, document.UpdateValues, ObjectChangeType.Updated, objectTypeName, "updateValues");
        AddChangeTypeValues(changeTypes, document.DeleteValues, ObjectChangeType.Deleted, objectTypeName, "deleteValues");

        // Deletions are the whole reason this mode is the recommended one, and a change log with no way
        // of stating one detects strictly less than a watermark column does at more cost.
        if (!changeTypes.ContainsValue(ObjectChangeType.Deleted))
            throw new SqlSchemaConfigurationException(
                $"Object Type '{objectTypeName}' has a change log with no 'deleteValues'. Observing deletions is what a change-log table is for; if this database cannot record them, use Watermark Column mode instead.");

        if (!changeTypes.ContainsValue(ObjectChangeType.Added) && !changeTypes.ContainsValue(ObjectChangeType.Updated))
            throw new SqlSchemaConfigurationException(
                $"Object Type '{objectTypeName}' has a change log with neither 'createValues' nor 'updateValues', so nothing but deletions could ever be imported from it.");

        return changeTypes;
    }

    private static void AddChangeTypeValues(
        Dictionary<string, ObjectChangeType> changeTypes,
        List<string?>? values,
        ObjectChangeType changeType,
        string objectTypeName,
        string fieldName)
    {
        if (values == null)
            return;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new SqlSchemaConfigurationException($"Object Type '{objectTypeName}' has an empty value in 'changeLog.{fieldName}'. A change-log row's change type is matched against these exactly, so a blank one could never match.");

            var trimmed = value.Trim();

            // Values are matched without regard to case, so a value meaning two things is ambiguous even
            // where the two spellings differ, and guessing which the administrator meant is not JIM's to do.
            if (changeTypes.TryGetValue(trimmed, out var existing))
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{objectTypeName}' has change-log value '{trimmed}' meaning both {existing} and {changeType}. Each value can only mean one kind of change.");

            changeTypes[trimmed] = changeType;
        }
    }

    /// <summary>
    /// Checks that every configured Object Type carries what the chosen Delta Import mode needs, which
    /// the parser cannot do on its own: the document is written without reference to the mode, and the
    /// same document is valid under one mode and unusable under the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every object type has to be covered, not just some. A Delta Import that silently skips an object
    /// type reports success while leaving that type's objects to drift, and no administrator would read
    /// a green Activity as "except for Groups".
    /// </para>
    /// <para>
    /// In Watermark Column mode that extends to every related table, for the same reason one step down:
    /// a membership added or revoked never moves the parent row's own watermark, so a related table with
    /// no watermark of its own is a source of changes JIM could never see. Permitting it would make
    /// undetected drift the default rather than a fault, so it is refused at save time; the alternative
    /// costs nothing to configure and everything to diagnose.
    /// </para>
    /// </remarks>
    /// <exception cref="SqlSchemaConfigurationException">An Object Type has nothing for this mode to read.</exception>
    /// <summary>
    /// Whether the Object Types selected for synchronisation carry what the configured Delta Import Mode
    /// needs to read them. Only the selected ones are asked (#1424): an unselected Object Type takes no part
    /// in any Run Profile, so a Delta Import cannot leave its objects to drift, and demanding a watermark
    /// column or a change log on a table JIM only ever writes to would be a schema change for nothing.
    /// </summary>
    /// <param name="mode">The mode the Connected System is configured for.</param>
    /// <param name="isSelected">Whether the named Object Type is selected for synchronisation.</param>
    /// <exception cref="SqlSchemaConfigurationException">A selected Object Type lacks what the mode needs.</exception>
    internal void ValidateDeltaImportMode(SqlDeltaImportMode mode, Func<string, bool> isSelected)
    {
        foreach (var objectType in ObjectTypes.Where(objectType => isSelected(objectType.Name)))
        {
            switch (mode)
            {
                case SqlDeltaImportMode.ChangeLogTable when objectType.ChangeLog == null:
                    throw new SqlSchemaConfigurationException(
                        $"{SqlConnectorConstants.SettingDeltaImportMode} is '{SqlConnectorConstants.DeltaImportModeChangeLogTable}', but Object Type '{objectType.Name}' is selected for synchronisation and has no 'changeLog'. " +
                        "A Delta Import that skipped an object type would report success while leaving its objects to drift, so every selected object type needs one. " +
                        "Give it a 'changeLog', or deselect it if JIM never imports from it.");

                case SqlDeltaImportMode.WatermarkColumn when objectType.WatermarkColumn == null:
                    throw new SqlSchemaConfigurationException(
                        $"{SqlConnectorConstants.SettingDeltaImportMode} is '{SqlConnectorConstants.DeltaImportModeWatermarkColumn}', but Object Type '{objectType.Name}' is selected for synchronisation and has no 'watermarkColumn'. " +
                        "A Delta Import that skipped an object type would report success while leaving its objects to drift, so every selected object type needs one. " +
                        "Give it a 'watermarkColumn', or deselect it if JIM never imports from it.");

                case SqlDeltaImportMode.WatermarkColumn:
                    ValidateRelatedTableWatermarkColumns(objectType);
                    break;
            }
        }
    }

    /// <exception cref="SqlSchemaConfigurationException">A related table has no watermark column for this mode to read.</exception>
    private static void ValidateRelatedTableWatermarkColumns(SqlObjectTypeConfiguration objectType)
    {
        foreach (var relatedTable in objectType.RelatedTables.Where(relatedTable => relatedTable.WatermarkColumn == null))
            throw new SqlSchemaConfigurationException(
                $"{SqlConnectorConstants.SettingDeltaImportMode} is '{SqlConnectorConstants.DeltaImportModeWatermarkColumn}', but Object Type '{objectType.Name}' has related table attribute '{relatedTable.AttributeName}' with no 'watermarkColumn'. " +
                $"A row added to or removed from '{relatedTable.TableName}' changes the object without touching its own row, so without a watermark column on the related table that change could never be detected.");
    }

    private static List<string> ReadAnchorColumns(SqlObjectTypeDocument document, string objectTypeName)
    {
        if (document.AnchorColumns == null || document.AnchorColumns.Count == 0)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{objectTypeName}' has no 'anchorColumns'. JIM identifies an object by its anchor, so at least one column must be named.") { Field = "anchorColumns" };

        var anchorColumns = new List<string>(document.AnchorColumns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchorColumn in document.AnchorColumns)
        {
            ValidateIdentifier(anchorColumn, objectTypeName, "anchorColumns");

            if (!seen.Add(anchorColumn!))
                throw new SqlSchemaConfigurationException($"Object Type '{objectTypeName}' lists anchor column '{anchorColumn}' more than once.");

            anchorColumns.Add(anchorColumn!);
        }

        return anchorColumns;
    }

    private static List<SqlColumnConfiguration> ReadColumns(SqlObjectTypeDocument document, string objectTypeName)
    {
        if (document.Columns == null)
            return [];

        var columns = new List<SqlColumnConfiguration>(document.Columns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in document.Columns)
        {
            ValidateIdentifier(column.Name, objectTypeName, "columns.name");

            if (string.IsNullOrWhiteSpace(column.ReferencesObjectType))
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{objectTypeName}' configures column '{column.Name}' without a 'referencesObjectType'. A column is only configured to say what its type cannot, which today means naming the object type it points at.");

            if (!seen.Add(column.Name!))
                throw new SqlSchemaConfigurationException($"Object Type '{objectTypeName}' configures column '{column.Name}' more than once.");

            columns.Add(new SqlColumnConfiguration { Name = column.Name!, ReferencesObjectType = column.ReferencesObjectType.Trim() });
        }

        return columns;
    }

    private static List<SqlRelatedTableConfiguration> ReadRelatedTables(SqlObjectTypeDocument document, string objectTypeName, int anchorColumnCount)
    {
        if (document.RelatedTables == null)
            return [];

        var relatedTables = new List<SqlRelatedTableConfiguration>(document.RelatedTables.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relatedTable in document.RelatedTables)
        {
            if (string.IsNullOrWhiteSpace(relatedTable.AttributeName))
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{objectTypeName}' has a related table with no 'attributeName'. The attribute name is what the multi-valued attribute is called on the object type.");

            var attributeName = relatedTable.AttributeName.Trim();

            if (!seen.Add(attributeName))
                throw new SqlSchemaConfigurationException($"Object Type '{objectTypeName}' declares related table attribute '{attributeName}' more than once.");

            ValidateIdentifier(relatedTable.Table, objectTypeName, $"relatedTables['{attributeName}'].table");
            ValidateIdentifier(relatedTable.ValueColumn, objectTypeName, $"relatedTables['{attributeName}'].valueColumn");

            if (!string.IsNullOrWhiteSpace(relatedTable.Schema))
                ValidateIdentifier(relatedTable.Schema, objectTypeName, $"relatedTables['{attributeName}'].schema");

            if (relatedTable.JoinColumns == null || relatedTable.JoinColumns.Count != anchorColumnCount)
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{objectTypeName}' joins related table attribute '{attributeName}' on {relatedTable.JoinColumns?.Count ?? 0} column(s), but the object type's anchor has {anchorColumnCount}. " +
                    "Joining on part of an anchor gathers another object's values onto this one, so 'joinColumns' must name one column per anchor column, in the same order.");

            foreach (var joinColumn in relatedTable.JoinColumns)
                ValidateIdentifier(joinColumn, objectTypeName, $"relatedTables['{attributeName}'].joinColumns");

            if (!string.IsNullOrWhiteSpace(relatedTable.WatermarkColumn))
                ValidateIdentifier(relatedTable.WatermarkColumn, objectTypeName, $"relatedTables['{attributeName}'].watermarkColumn");

            relatedTables.Add(new SqlRelatedTableConfiguration
            {
                AttributeName = attributeName,
                SchemaName = string.IsNullOrWhiteSpace(relatedTable.Schema) ? null : relatedTable.Schema,
                TableName = relatedTable.Table!,
                ValueColumn = relatedTable.ValueColumn!,
                JoinColumns = [.. relatedTable.JoinColumns.Select(joinColumn => joinColumn!)],
                ReferencesObjectType = string.IsNullOrWhiteSpace(relatedTable.ReferencesObjectType) ? null : relatedTable.ReferencesObjectType.Trim(),
                WatermarkColumn = string.IsNullOrWhiteSpace(relatedTable.WatermarkColumn) ? null : relatedTable.WatermarkColumn
            });
        }

        return relatedTables;
    }

    private static void ValidateReferences(SqlObjectTypeConfiguration objectType, HashSet<string> names)
    {
        foreach (var column in objectType.Columns.Where(column => !names.Contains(column.ReferencesObjectType)))
            throw new SqlSchemaConfigurationException(
                $"Object Type '{objectType.Name}' says column '{column.Name}' references object type '{column.ReferencesObjectType}', which this document does not declare. A reference can only point at an object type JIM also synchronises.") { Field = "columns" };

        foreach (var relatedTable in objectType.RelatedTables.Where(relatedTable => relatedTable.ReferencesObjectType != null && !names.Contains(relatedTable.ReferencesObjectType)))
            throw new SqlSchemaConfigurationException(
                $"Object Type '{objectType.Name}' says related table attribute '{relatedTable.AttributeName}' references object type '{relatedTable.ReferencesObjectType}', which this document does not declare.") { Field = "relatedTables" };
    }

    /// <summary>
    /// Refuses an identifier that could not name a real database object. Surrounding whitespace is
    /// refused too, which <see cref="SqlIdentifier"/> itself tolerates: quoting " EMPLOYEES" produces a
    /// genuinely different object name, so a pasted-in space would otherwise surface much later as a
    /// table the account cannot see.
    /// </summary>
    private static void ValidateIdentifier(string? identifier, string objectTypeName, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new SqlSchemaConfigurationException($"Object Type '{objectTypeName}' has no value for '{fieldName}'.") { Field = fieldName };

        if (identifier.Trim() != identifier)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{objectTypeName}' has '{fieldName}' set to '{identifier}', which starts or ends with whitespace. Database identifiers are used exactly as written, so the spaces would name a different object.") { Field = fieldName };

        try
        {
            SqlIdentifier.Validate(identifier, fieldName);
        }
        catch (ArgumentException ex)
        {
            throw new SqlSchemaConfigurationException($"Object Type '{objectTypeName}' has '{fieldName}' set to a value that cannot name a database object: {ex.Message}", ex) { Field = fieldName };
        }
    }

    /// <summary>
    /// Refuses a statement that is not a single query. Connector configuration is privileged
    /// administrator input, so this is not a defence against a hostile administrator; it catches the
    /// accidental paste, and keeps one statement one statement.
    /// </summary>
    private static void ValidateSelectStatement(string statement, string objectTypeName)
    {
        switch (CheckSelectStatement(statement))
        {
            case SelectStatementProblem.NotAQuery:
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{objectTypeName}' has a 'select' that does not begin with SELECT or WITH. JIM reads objects with it, so it has to be a query.") { Field = "select" };

            case SelectStatementProblem.MoreThanOneStatement:
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{objectTypeName}' has a 'select' containing a semicolon. It must be a single statement, with no terminator.") { Field = "select" };
        }
    }

    /// <summary>
    /// The one rule for what may be run as an Object Type's source query, shared by the parser and by the
    /// catalogue's description of a statement so the two can never accept different things.
    /// </summary>
    /// <returns>Null where the statement is acceptable.</returns>
    internal static SelectStatementProblem? CheckSelectStatement(string statement)
    {
        var trimmed = statement.Trim();

        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            return SelectStatementProblem.NotAQuery;

        return trimmed.Contains(';', StringComparison.Ordinal) ? SelectStatementProblem.MoreThanOneStatement : null;
    }

    internal enum SelectStatementProblem
    {
        NotAQuery,
        MoreThanOneStatement
    }
}
