// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors;

public static class ConnectorConstants
{
    // these connector name constants are used to help identify built-in connectors that haven't been updated from NuGet packages.
    // this enables a JIM instance to function that hasn't, or isn't able to be (disconnected environments) updated connectors from NuGet packages.

    public static string LdapConnectorName => "JIM LDAP Connector";
    public static string FileConnectorName => "JIM File Connector";
    // the SCIM 2.0 connectors are a deliberate pair named by JIM's role in the exchange, per RFC 7644 terms:
    // the Client Connector connects out to external SCIM service providers (#545); the Service Provider
    // Connector is the pseudo-connector for JIM's own inbound SCIM server surface (#124, not yet implemented).
    public static string ScimClientConnectorName => "JIM SCIM 2.0 Client Connector";
    public static string ScimServiceProviderConnectorName => "JIM SCIM 2.0 Service Provider Connector";
    public static string SqlConnectorName => "JIM SQL Connector";
}