// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Resources;
using JIM.Models.Localisation;

namespace JIM.Connectors;

/// <summary>
/// What a certificate message calls the far end ("directory server", "SCIM service provider"), as a message argument so
/// the portal can say it in the viewer's language (#3030). A description this does not know (a third-party Connector's)
/// is passed as it is.
/// </summary>
internal static class ServerNoun
{
    public static MessageArgument For(string description) => description switch
    {
        "SCIM service provider" => JimMessage.Create(ConnectorMessageCatalogues.Connectors, nameof(ConnectorMessages.CertServerScimServiceProvider)),
        "directory server" => JimMessage.Create(ConnectorMessageCatalogues.Connectors, nameof(ConnectorMessages.CertServerDirectoryServer)),
        "database server" => JimMessage.Create(ConnectorMessageCatalogues.Connectors, nameof(ConnectorMessages.CertServerDatabaseServer)),
        _ => description
    };
}
