// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>The steps of a Microsoft Graph import, as an administrator watching the run sees them.</summary>
public static class GraphConnectorPhases
{
    public const string Prepare = "graph-prepare";
    public const string PrepareName = "Preparing";
    public const string Fetch = "graph-fetch";
    public const string FetchName = "Fetching";
}
