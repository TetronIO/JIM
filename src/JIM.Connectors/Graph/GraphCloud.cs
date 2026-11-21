// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>
/// The endpoints of one Microsoft cloud: where tokens are issued and where Microsoft Graph is served. Tokens are not
/// interchangeable between clouds, so the token scope is derived from the same Graph host the requests go to.
/// </summary>
/// <remarks>
/// Endpoints from Microsoft's national cloud deployments reference (learn.microsoft.com/graph/deployments). Microsoft 365
/// GCC (Moderate) runs on the global endpoints, so it has no entry of its own. US Government L4 and L5 share a token
/// service but not a Graph host, which is the pairing most easily got wrong.
/// </remarks>
internal sealed record GraphCloud(string Name, Uri AuthorityHost, Uri GraphHost)
{
    public static readonly GraphCloud Global = new(
        GraphConnectorConstants.CloudGlobal,
        new Uri("https://login.microsoftonline.com/"),
        new Uri("https://graph.microsoft.com/"));

    public static readonly GraphCloud UsGovernmentL4 = new(
        GraphConnectorConstants.CloudUsGovernmentL4,
        new Uri("https://login.microsoftonline.us/"),
        new Uri("https://graph.microsoft.us/"));

    public static readonly GraphCloud UsGovernmentL5 = new(
        GraphConnectorConstants.CloudUsGovernmentL5,
        new Uri("https://login.microsoftonline.us/"),
        new Uri("https://dod-graph.microsoft.us/"));

    public static readonly GraphCloud China = new(
        GraphConnectorConstants.CloudChina,
        new Uri("https://login.chinacloudapi.cn/"),
        new Uri("https://microsoftgraph.chinacloudapi.cn/"));

    /// <summary>Every cloud, in the order the Cloud drop-down offers them.</summary>
    public static IReadOnlyList<GraphCloud> All { get; } = [Global, UsGovernmentL4, UsGovernmentL5, China];

    /// <summary>The client credentials scope for this cloud's Graph: its host followed by <c>/.default</c>.</summary>
    public string Scope => $"{GraphHost.GetLeftPart(UriPartial.Authority)}/.default";

    /// <summary>The base of every Graph request: the v1.0 endpoint on this cloud's Graph host.</summary>
    public Uri ApiBaseUrl => new(GraphHost, "v1.0/");

    /// <summary>The tenant's OAuth 2.0 v2.0 token endpoint on this cloud's token service.</summary>
    public Uri TokenEndpoint(string tenantId) => new(AuthorityHost, $"{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token");

    /// <summary>
    /// The cloud a Cloud setting value names. An unset value means Global, the setting's default; an unrecognised one
    /// returns false rather than falling back, because requests sent to the wrong cloud fail with a misleading error.
    /// </summary>
    public static bool TryResolve(string? name, out GraphCloud cloud)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            cloud = Global;
            return true;
        }

        var match = All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        cloud = match ?? Global;
        return match != null;
    }
}
