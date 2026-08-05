// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql;

/// <summary>
/// The phases the JIM SQL Connector performs inside the JIM phase that calls it, and the labels an
/// administrator sees for them (#454). Declared through <see cref="JIM.Models.Interfaces.IConnectorPhases"/>
/// so the steps show up before they run, and entered by key as the work progresses.
/// </summary>
internal static class SqlConnectorPhases
{
    /// <summary>
    /// Asking the database how many rows a Full Import is about to read. A separate query, and on a
    /// large view a slow one, but it is what turns the fetch into a percentage and a time remaining.
    /// </summary>
    internal const string Count = "count";

    internal const string CountName = "Counting rows";

    /// <summary>
    /// Reading what has changed since the last import, from the change-log table or beyond the
    /// watermark. Delta Imports only, and separate from the fetch because the rows themselves are read
    /// afterwards.
    /// </summary>
    internal const string QueryChanges = "query-changes";

    internal const string QueryChangesName = "Querying changes";

    /// <summary>
    /// Reading rows, a page at a time, for each configured object type.
    /// </summary>
    internal const string Fetch = "fetch";

    internal const string FetchName = "Fetching rows";
}
