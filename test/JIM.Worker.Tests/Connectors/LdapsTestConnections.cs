// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Connectors;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Connection helpers shared by the <c>RequiresLdaps</c> fixtures (<see cref="LdapsCertificateValidationTests"/>,
/// <see cref="SambaAdAndUnencryptedLdapTests"/> and <see cref="DirectoryServer389LdapTests"/>), so each directory
/// type's fixture carries only what differs: its environment variables and the failure reasons it expects.
/// </summary>
internal static class LdapsTestConnections
{
    /// <summary>
    /// Opens an import connection the way the synchronisation engine does, with the supplied certificates standing
    /// in for the JIM certificate store.
    /// </summary>
    internal static void OpenConnection(string host, int port, bool useSecureConnection, string username, string password, ILogger logger, params string[] trustedCertificatePaths)
    {
        using var connector = new LdapConnector();
        connector.SetCertificateProvider(new FakeCertificateProvider(trustedCertificatePaths));

        try
        {
            connector.OpenImportConnection(BuildSettingValues(host, port, useSecureConnection, username, password), null, logger);
        }
        finally
        {
            connector.CloseImportConnection();
        }
    }

    internal static List<ConnectedSystemSettingValue> BuildSettingValues(string host, int port, bool useSecureConnection, string username, string password)
    {
        return
        [
            NewSetting("Host", stringValue: host),
            NewSetting("Port", intValue: port),
            NewSetting("Use Secure Connection (LDAPS)?", checkboxValue: useSecureConnection),
            NewSetting("Connection Timeout", intValue: 10),
            NewSetting("Username", stringValue: username),
            NewSetting("Password", encryptedValue: password),
            NewSetting("Authentication Type", stringValue: "Simple"),
            // One attempt only: a rejected certificate reports as a down server, which the connector treats as
            // transient, and retrying it just multiplies the wait before the test can assert.
            NewSetting("Maximum Retries", intValue: 0)
        ];
    }

    private static ConnectedSystemSettingValue NewSetting(string name, string? stringValue = null, string? encryptedValue = null, int? intValue = null, bool checkboxValue = false)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue,
            StringEncryptedValue = encryptedValue,
            IntValue = intValue,
            CheckboxValue = checkboxValue
        };
    }

    /// <summary>
    /// Supplies certificates from PEM files in place of the JIM certificate store.
    /// </summary>
    internal sealed class FakeCertificateProvider : ICertificateProvider
    {
        private readonly string[] _certificatePaths;

        internal FakeCertificateProvider(string[] certificatePaths)
        {
            _certificatePaths = certificatePaths;
        }

        public Task<List<X509Certificate2>> GetTrustedCertificatesAsync()
        {
            return Task.FromResult(_certificatePaths
                .Select(X509CertificateLoader.LoadCertificateFromFile)
                .ToList());
        }
    }
}
