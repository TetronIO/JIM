// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Builds the relative request URL for a page of resources (RFC 7644 section 3.4.2).
/// </summary>
public static class ScimQueryBuilder
{
    /// <summary>
    /// Builds the query for one page.
    /// </summary>
    /// <param name="endpoint">The resource endpoint, as the provider published it (for example <c>/Users</c>).</param>
    /// <param name="position">Where the walk has got to.</param>
    /// <param name="pageSize">The Run Profile's page size, sent as <c>count</c>.</param>
    /// <param name="filter">An optional SCIM filter, used by delta import.</param>
    /// <param name="excludedAttributes">Attributes the administrator does not want returned.</param>
    public static string BuildPageQuery(
        string endpoint,
        ScimImportPosition position,
        int pageSize,
        string? filter = null,
        IReadOnlyList<string>? excludedAttributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(position);

        var query = new StringBuilder(NormaliseEndpoint(endpoint));
        var separator = '?';

        void Append(string name, string value)
        {
            query.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        if (pageSize > 0)
            Append("count", pageSize.ToString());

        if (position.Mode == ScimPaginationMode.Cursor)
        {
            // RFC 9865: the first request asks for cursor paging by sending an empty cursor, and the
            // provider answers with the cursor for the page after this one.
            Append("cursor", position.Cursor ?? string.Empty);
        }
        else
        {
            Append("startIndex", position.StartIndex.ToString());
        }

        if (!string.IsNullOrWhiteSpace(filter))
            Append("filter", filter);

        // RFC 7644 section 3.9 makes attributes and excludedAttributes mutually exclusive. JIM sends only
        // excludedAttributes: naming an inclusive set risks a provider returning nothing else, and the
        // attributes an administrator has not selected yet still need to be importable the moment they do.
        if (excludedAttributes is { Count: > 0 })
            Append("excludedAttributes", string.Join(',', excludedAttributes));

        return query.ToString();
    }

    /// <summary>
    /// Turns a published endpoint into a path relative to the base URL. Providers publish endpoints with
    /// a leading slash (RFC 7643 section 6), which would otherwise compose as an absolute path and drop
    /// the base URL's own prefix.
    /// </summary>
    public static string NormaliseEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return endpoint.TrimStart('/');
    }

    /// <summary>
    /// The attributes import cannot work without: <c>id</c> anchors every Connected System Object, and
    /// <c>meta</c> carries the last-modified date delta import watermarks against. Excluding either
    /// would break importing in a way that looks like a provider fault, so an administrator naming them
    /// is ignored rather than obeyed.
    /// </summary>
    private static readonly string[] NeverExcluded = ["id", "meta"];

    /// <summary>
    /// Splits an administrator's comma or newline separated Excluded Attributes setting into names.
    /// </summary>
    public static List<string> ParseExcludedAttributes(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return [];

        return setting
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !NeverExcluded.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
