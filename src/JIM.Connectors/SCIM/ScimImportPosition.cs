// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using JIM.Models.Staging;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Where an import has got to, carried between the repeated <c>ImportAsync</c> calls JIM makes.
/// <para>
/// One position covers the whole import rather than one token per resource type, because the connector
/// walks the resource types in order: the index of the type being read is part of the position. JIM
/// stops calling when no tokens come back, so the absence of this token is what says "finished".
/// </para>
/// </summary>
public class ScimImportPosition
{
    /// <summary>
    /// The name of the pagination token the position travels in.
    /// </summary>
    public const string TokenName = "ScimImportPosition";

    /// <summary>
    /// Which resource type is being read, as an index into the run's ordered resource type list.
    /// </summary>
    [JsonPropertyName("resourceTypeIndex")]
    public int ResourceTypeIndex { get; set; }

    /// <summary>
    /// The 1-based index of the next resource to ask for, under index-based paging. RFC 7644 numbers
    /// resources from 1, not 0.
    /// </summary>
    [JsonPropertyName("startIndex")]
    public int StartIndex { get; set; } = 1;

    /// <summary>
    /// The opaque cursor for the next page, under cursor-based paging.
    /// </summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }

    /// <summary>
    /// How many pages of the current resource type have been read. Carried only to bound a walk against
    /// a provider that never runs out; it is reset when the walk moves to the next resource type.
    /// </summary>
    [JsonPropertyName("pagesRead")]
    public int PagesRead { get; set; }

    /// <summary>
    /// The paging style in force. Under <see cref="ScimPaginationMode.Auto"/> this starts as
    /// <see cref="ScimPaginationMode.Index"/> and becomes <see cref="ScimPaginationMode.Cursor"/> if the
    /// provider volunteers a cursor.
    /// </summary>
    [JsonPropertyName("mode")]
    public ScimPaginationMode Mode { get; set; } = ScimPaginationMode.Index;

    /// <summary>
    /// Reads the position back from the tokens JIM replayed, or returns a fresh position at the start of
    /// the first resource type when there are none.
    /// </summary>
    /// <param name="tokens">The pagination tokens returned by the previous call.</param>
    /// <param name="configuredMode">The administrator's Pagination Mode setting, used for a fresh position.</param>
    public static ScimImportPosition FromTokens(List<ConnectedSystemPaginationToken> tokens, ScimPaginationMode configuredMode)
    {
        var token = tokens?.SingleOrDefault(t => t.Name == TokenName)?.StringValue;
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ScimImportPosition
            {
                // Auto opens index-based, because that is the paging style RFC 7644 makes mandatory.
                Mode = configuredMode == ScimPaginationMode.Cursor ? ScimPaginationMode.Cursor : ScimPaginationMode.Index
            };
        }

        try
        {
            return JsonSerializer.Deserialize<ScimImportPosition>(token) ?? new ScimImportPosition();
        }
        catch (JsonException)
        {
            // An unreadable token would otherwise restart the import silently partway through, which
            // would look like a successful run that imported a fraction of the data.
            throw new InvalidOperationException(
                "The SCIM import pagination token could not be read, so the import cannot resume from where it left off.");
        }
    }

    /// <summary>
    /// Renders the position as the single pagination token JIM replays on the next call.
    /// </summary>
    public ConnectedSystemPaginationToken ToToken()
    {
        return new ConnectedSystemPaginationToken(TokenName, JsonSerializer.Serialize(this));
    }
}
