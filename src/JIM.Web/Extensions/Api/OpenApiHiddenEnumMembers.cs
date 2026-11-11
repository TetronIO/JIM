// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Models.Transactional;
using Microsoft.OpenApi;

namespace JIM.Web.Extensions.Api;

/// <summary>
/// Leaves out of the published OpenAPI document the enum members no API caller can meet or use.
/// </summary>
/// <remarks>
/// <see cref="PendingExportStatus.Incomplete"/> is the one today (#2919): the status of a Pending Export JIM is still
/// writing, which no response carries and which a status filter refuses with 400, so publishing it would only offer
/// callers a value they cannot use.
/// </remarks>
public static class OpenApiHiddenEnumMembers
{
    private static readonly Dictionary<Type, string[]> Hidden = new()
    {
        [typeof(PendingExportStatus)] = [nameof(PendingExportStatus.Incomplete)]
    };

    /// <summary>
    /// Removes the hidden members from the schema generated for <paramref name="type"/>, where it is (or wraps) an enum
    /// with any.
    /// </summary>
    public static void ApplyToSchema(OpenApiSchema schema, Type type)
    {
        var enumType = Nullable.GetUnderlyingType(type) ?? type;
        if (!Hidden.TryGetValue(enumType, out var hiddenNames) || schema.Enum is not { Count: > 0 } members)
            return;

        for (var i = members.Count - 1; i >= 0; i--)
        {
            if (members[i] is JsonValue member && member.TryGetValue<string>(out var name) && hiddenNames.Contains(name))
                members.RemoveAt(i);
        }
    }
}
