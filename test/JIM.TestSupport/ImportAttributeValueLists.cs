// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Models.Staging;

namespace JIM.TestSupport;

/// <summary>
/// Reads which typed value lists a <see cref="ConnectedSystemImportObjectAttribute"/> has actually allocated (#2521), by
/// its backing fields, so a test can tell a list that was never made from one made and left empty. Reading the public
/// list properties cannot answer this: each allocates its list on first read.
/// </summary>
public static class ImportAttributeValueLists
{
    private static readonly FieldInfo[] ListFields = typeof(ConnectedSystemImportObjectAttribute)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Where(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(List<>))
        .ToArray();

    /// <summary>
    /// The names of every typed value list the attribute holds an allocated list for, as their properties are named
    /// (e.g. <c>StringValues</c>), in ordinal order.
    /// </summary>
    public static string[] Allocated(ConnectedSystemImportObjectAttribute attribute) =>
        ListFields
            .Where(field => field.GetValue(attribute) != null)
            .Select(field => PropertyNameOf(field.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The names of every typed value list the attribute declares, as their properties are named.</summary>
    public static string[] All() =>
        ListFields.Select(field => PropertyNameOf(field.Name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    // An auto-property's backing field is named "<StringValues>k__BackingField"; a hand-written one "_stringValues".
    private static string PropertyNameOf(string fieldName)
    {
        if (fieldName.StartsWith('<'))
            return fieldName[1..fieldName.IndexOf('>')];

        var trimmed = fieldName.TrimStart('_');
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
