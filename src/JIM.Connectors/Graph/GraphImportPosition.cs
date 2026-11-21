// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Staging;

namespace JIM.Connectors.Graph;

/// <summary>
/// Where an import has got to, carried between pages as JIM's pagination token: which Object Type, and either the link
/// to that type's next page (a full read; no link means the type has not been started) or how many of its changes have
/// been staged (a Delta Import).
/// </summary>
internal sealed record GraphImportPosition(int TargetIndex, string? NextLink, int ChangesStaged = 0)
{
    private const string TokenName = "GraphImportPosition";

    public static readonly GraphImportPosition Start = new(0, null);

    public ConnectedSystemPaginationToken ToToken() => new(TokenName, JsonSerializer.Serialize(this));

    public static GraphImportPosition FromTokens(List<ConnectedSystemPaginationToken>? tokens)
    {
        var token = tokens?.FirstOrDefault(t => t.Name == TokenName)?.StringValue;
        return token == null ? Start : JsonSerializer.Deserialize<GraphImportPosition>(token) ?? Start;
    }
}
