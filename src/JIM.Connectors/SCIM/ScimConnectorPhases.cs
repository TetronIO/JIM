// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// The phases the SCIM Connector performs inside the JIM phase that calls it, and the labels an
/// administrator sees for them (#454). Declared through <see cref="JIM.Models.Interfaces.IConnectorPhases"/>
/// so the steps show up before they run, and entered by key as the work progresses.
/// </summary>
internal static class ScimConnectorPhases
{
    /// <summary>
    /// Asking the service provider what it is: its capabilities, its resource types, and its schemas.
    /// Read fresh at the start of every run rather than persisted, so a provider that gained or lost a
    /// feature is followed immediately; that makes it a real round trip worth showing as a step.
    /// </summary>
    internal const string Discover = "discover";

    internal const string DiscoverName = "Discovering the service provider";

    /// <summary>
    /// Reading resources, a page at a time, from each endpoint the selected object types map to.
    /// </summary>
    internal const string Fetch = "fetch";

    internal const string FetchName = "Fetching resources";
}
