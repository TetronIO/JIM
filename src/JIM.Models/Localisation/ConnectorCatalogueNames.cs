// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// How a Connector's message catalogue is named (#3103): <c>connector:&lt;identifier&gt;/Messages</c>, such as
/// <c>connector:jim.ldap/Messages</c>. The name is stored with every message the Connector writes, so it is the same
/// whether the catalogue ships inside JIM or arrives with an installed Connector.
/// </summary>
public static class ConnectorCatalogueNames
{
    private const string Prefix = "connector:";
    private const string Suffix = "/Messages";

    /// <summary>The catalogue of the Connector whose identifier is <paramref name="identifier"/>.</summary>
    public static string For(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return Prefix + identifier + Suffix;
    }

    /// <summary>Whether <paramref name="catalogue"/> is a Connector's, rather than one of JIM's own.</summary>
    public static bool IsConnectorCatalogue(string catalogue) =>
        catalogue.StartsWith(Prefix, StringComparison.Ordinal);
}
