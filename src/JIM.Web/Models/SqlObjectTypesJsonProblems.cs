// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.Json;
using JIM.Connectors.Sql;

namespace JIM.Web.Models;

/// <summary>
/// Places each of the parser's problems with a SQL Object Types document on a line, for the JSON view (#2570).
/// A fault in the JSON itself is on the line the parser stopped at. A problem with what the document says is on
/// the line of the field it concerns, or, where that field is missing, the line its Object Type opens on.
/// </summary>
public static class SqlObjectTypesJsonProblems
{
    /// <summary>
    /// Every problem with the document, in line order, with the problems that have no line first.
    /// </summary>
    public static IReadOnlyList<SqlObjectTypesJsonProblem> Locate(string? json)
    {
        var problems = SqlObjectTypesDocument.Validate(json);
        if (problems.Count == 0)
            return [];

        // Problems with what the document says only arise once it has been read as JSON, so the layout below can
        // always be read when one needs placing.
        var layout = problems.Any(problem => problem.LineNumber == null && problem.ObjectTypeIndex != null)
            ? ReadLayout(json!)
            : null;

        return [.. problems
            .Select(problem => new SqlObjectTypesJsonProblem(problem.Message, LineOf(problem, layout), problem.LineNumber.HasValue))
            .OrderBy(problem => problem.Line ?? 0)];
    }

    private static int? LineOf(SqlObjectTypesProblem problem, IReadOnlyList<ObjectTypeLayout>? layout)
    {
        if (problem.LineNumber.HasValue)
            return (int)problem.LineNumber.Value;

        if (problem.ObjectTypeIndex is not { } index || layout == null || index >= layout.Count)
            return null;

        var objectType = layout[index];
        var field = TopLevelField(problem.Field);
        return field != null && objectType.FieldLines.TryGetValue(field, out var fieldLine) ? fieldLine : objectType.Line;
    }

    /// <summary>
    /// The Object Type's own field a problem's field belongs to: 'changeLog' for 'changeLog.table', 'relatedTables'
    /// for "relatedTables['Members'].watermarkColumn".
    /// </summary>
    private static string? TopLevelField(string? field)
    {
        if (string.IsNullOrEmpty(field))
            return null;

        var end = field.IndexOfAny(['.', '[']);
        return end < 0 ? field : field[..end];
    }

    /// <summary>
    /// Where each Object Type in the 'objectTypes' list opens, and where each of its fields is named.
    /// </summary>
    private static List<ObjectTypeLayout> ReadLayout(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var objectTypes = new List<ObjectTypeLayout>();
        var lineStarts = new LineIndex(bytes);
        var inObjectTypes = false;
        ObjectTypeLayout? current = null;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                        inObjectTypes = string.Equals(reader.GetString(), "objectTypes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case JsonTokenType.StartObject when inObjectTypes && reader.CurrentDepth == 2:
                        current = new ObjectTypeLayout(lineStarts.LineOf(reader.TokenStartIndex));
                        objectTypes.Add(current);
                        break;
                    case JsonTokenType.Null when inObjectTypes && reader.CurrentDepth == 2:
                        objectTypes.Add(new ObjectTypeLayout(lineStarts.LineOf(reader.TokenStartIndex)));
                        break;
                    case JsonTokenType.EndObject when reader.CurrentDepth == 2:
                        current = null;
                        break;
                    case JsonTokenType.PropertyName when current != null && reader.CurrentDepth == 3:
                        current.FieldLines.TryAdd(reader.GetString()!, lineStarts.LineOf(reader.TokenStartIndex));
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // The parser read this document, so this reader can too; were it ever not to, the Object Types read
            // before the fault keep their lines and the rest fall back to having none.
        }

        return objectTypes;
    }

    private sealed class ObjectTypeLayout(int line)
    {
        public int Line { get; } = line;

        public Dictionary<string, int> FieldLines { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns a byte offset into the one-based line it falls on.
    /// </summary>
    private sealed class LineIndex
    {
        private readonly List<long> _lineStarts = [0];

        public LineIndex(byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n')
                    _lineStarts.Add(i + 1);
            }
        }

        public int LineOf(long offset)
        {
            var found = _lineStarts.BinarySearch(offset);
            return (found >= 0 ? found : ~found - 1) + 1;
        }
    }
}
