// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Connectors.Graph;

/// <summary>
/// What <see cref="GraphObjectReader"/> read from one Graph object: its id, the attribute values it could convert, and
/// each value it could not, with why, so the import can report them rather than drop them.
/// </summary>
internal sealed class GraphObjectReadResult
{
    public string? Id { get; init; }

    public List<ConnectedSystemImportObjectAttribute> Attributes { get; } = [];

    public List<GraphUnreadableValue> UnreadableValues { get; } = [];
}
