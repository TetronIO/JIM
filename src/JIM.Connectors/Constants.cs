namespace JIM.Connectors
{
    public static class Constants
    {
        // these connector name constants are used to help identify built-in connectors that haven't been updated from NuGet packages.
        // this enables a JIM instance to function that hasn't, or isn't able to be (disconnected environments) updated connectors from NuGet packages.

        public static string LdapConnectorName => "JIM LDAP Connector";
        public static string CsvConnectorName = "JIM CSV Connector";
        public static string Scim2ConnectorName = "JIM SCIM2 Connector";
        public static string SqlConnectorName = "JIM SQL Connector";
    }
}
