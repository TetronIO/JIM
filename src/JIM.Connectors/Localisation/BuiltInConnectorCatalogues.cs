// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Connectors;

/// <summary>
/// The built-in Connectors' own message catalogues (#3103), one each, named for the Connector's identifier
/// (<see cref="ConnectorCatalogueNames"/>) exactly as an installed Connector's will be. Each is a resource file under
/// <c>Resources/</c> (<c>LdapConnectorMessages.resx</c> for <see cref="Ldap"/>), so its keys are checked at build time:
/// <c>JimMessage.Create(BuiltInConnectorCatalogues.Ldap, nameof(LdapConnectorMessages.SomeKey), ...)</c>.
/// </summary>
public static class BuiltInConnectorCatalogues
{
    private static readonly object Gate = new();
    private static bool _registered;

    public const string Ldap = "connector:" + ConnectorConstants.LdapConnectorIdentifier + "/Messages";
    public const string File = "connector:" + ConnectorConstants.FileConnectorIdentifier + "/Messages";
    public const string ScimClient = "connector:" + ConnectorConstants.ScimClientConnectorIdentifier + "/Messages";
    public const string ScimServiceProvider = "connector:" + ConnectorConstants.ScimServiceProviderConnectorIdentifier + "/Messages";
    public const string Sql = "connector:" + ConnectorConstants.SqlConnectorIdentifier + "/Messages";
    public const string Graph = "connector:" + ConnectorConstants.GraphConnectorIdentifier + "/Messages";

    /// <summary>Each built-in catalogue and the resource file under <c>Resources/</c> that holds it.</summary>
    public static IReadOnlyDictionary<string, string> ResourceFiles { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Ldap] = "LdapConnectorMessages",
        [File] = "FileConnectorMessages",
        [ScimClient] = "ScimClientConnectorMessages",
        [ScimServiceProvider] = "ScimServiceProviderConnectorMessages",
        [Sql] = "SqlConnectorMessages",
        [Graph] = "GraphConnectorMessages"
    };

    /// <summary>
    /// Makes the built-in catalogues available to <see cref="MessageRenderer"/> for the life of the process. Safe to call
    /// any number of times; <c>JimApplication</c> calls it, so every host has them.
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
                return;

            // held for the life of the process, so the registration is never disposed
            _ = MessageCatalogueSources.Register(new BuiltInConnectorCatalogueSource());
            _registered = true;
        }
    }
}
