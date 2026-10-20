// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;
namespace JIM.Connectors.File;

/// <summary>
/// The one rule a delimited file's header row must meet on every path that reads it (schema discovery, import and
/// export): each column names a different attribute. Columns are matched to attributes ignoring case (#2051), so two
/// columns whose names differ only in case cannot be told apart, and a repeated column is not a way to hold several
/// values: those go in one column, separated by the Multi-Value Delimiter (#2186).
/// </summary>
internal static class FileConnectorHeader
{
    /// <summary>
    /// Why the header row cannot be read, naming each offending column, or null when every column names a different
    /// attribute.
    /// </summary>
    internal static string? FindProblem(IEnumerable<string> header, string fileName, string multiValueDelimiter)
    {
        var clashes = header
            .GroupBy(column => IdentifierKey.For(column, caseExact: false), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Distinct(StringComparer.Ordinal).ToList())
            .ToList();
        if (clashes.Count == 0)
            return null;

        var repeated = clashes.Where(names => names.Count == 1).Select(names => $"'{names[0]}'").ToList();
        if (repeated.Count > 0)
            return $"The file '{fileName}' repeats the column {string.Join(", ", repeated)}. Each attribute has one column: hold " +
                   $"several values in it, separated by the Multi-Value Delimiter ({multiValueDelimiter}), and mark the attribute " +
                   "multi-valued on the Connected System's Schema tab.";

        var caseOnly = clashes.Select(names => string.Join(", ", names.Select(name => $"'{name}'")));
        return $"The file '{fileName}' has columns whose names differ only in case ({string.Join("; ", caseOnly)}). " +
               "Columns are matched to attributes ignoring case, so JIM cannot tell which to read. Rename or remove one of each.";
    }
}
