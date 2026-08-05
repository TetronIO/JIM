// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How the JIM SQL Connector treats a database server's certificate.
/// <para>
/// Two rules decide every case here. Certificates an administrator added in Admin &gt; Certificates are
/// additional trust anchors, never a replacement for the operating system's own bundle, so the ordinary
/// attempt always happens first and only a certificate the JIM certificate store vouches for is ever
/// accepted afterwards. And a refused certificate is reported as itself, with its details, rather than
/// as a connectivity error an administrator cannot act on.
/// </para>
/// </summary>
[TestFixture]
public class SqlConnectorCertificateTrustTests
{
    private ILogger _logger = null!;
    private X509Certificate2 _serverCertificate = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _serverCertificate = CreateSelfSignedCertificate("CN=db.example.com");
    }

    [TearDown]
    public void TearDown()
    {
        _serverCertificate.Dispose();
        (_logger as IDisposable)?.Dispose();
    }

    [Test]
    public void ValidateSettingValues_RefusedCertificateVouchedForByTheJimCertificateStore_RetriesWithItAsAnAdditionalAnchor()
    {
        var provider = new FakeSqlProvider { SucceedsOnlyWithAPinnedCertificate = true };
        using var connector = CreateConnector(provider, SoundCertificateReading(), trustedCertificate: _serverCertificate);

        var results = connector.ValidateSettingValues(CreateSettingValues(), _logger);

        Assert.That(results, Is.Empty, "The certificate is one the administrator added, so the connection is expected to succeed on the second attempt.");
        Assert.That(provider.BuiltConnectionSettings, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(provider.BuiltConnectionSettings[0].PinnedServerCertificatePath, Is.Null,
                "The operating system's own trust anchors must always get the first say; JIM's store can only ever add to them.");
            Assert.That(provider.BuiltConnectionSettings[1].PinnedServerCertificatePath, Is.Not.Null);
            Assert.That(System.IO.File.Exists(provider.BuiltConnectionSettings[1].PinnedServerCertificatePath!), Is.True,
                "The driver takes a trust anchor as a path, so the certificate has to exist on disk while the connection lives.");
        });
    }

    [Test]
    public void ValidateSettingValues_RefusedCertificateNothingVouchesFor_ReportsTheCertificateItself()
    {
        var provider = new FakeSqlProvider { OpenFailure = new FakeDbException("A connection was successfully established with the server, but then an error occurred.") };
        using var connector = CreateConnector(provider, UntrustedCertificateReading(), trustedCertificate: null);

        var results = connector.ValidateSettingValues(CreateSettingValues(), _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Exception, Is.TypeOf<ServerCertificateRejectedException>(),
                "A refused certificate has to reach the administrator as the certificate it was, not as a generic connectivity error.");
            Assert.That(((ServerCertificateRejectedException)results[0].Exception!).Diagnostic.Thumbprint, Is.EqualTo(_serverCertificate.Thumbprint));
            Assert.That(provider.BuiltConnectionSettings, Has.Count.EqualTo(1), "Nothing vouches for the certificate, so there is nothing to retry with.");
        });
    }

    [Test]
    public void ValidateSettingValues_CertificateSoundButNotInTheJimCertificateStore_DoesNotAcceptIt()
    {
        // The certificate passes every check JIM makes and the JIM certificate store holds nothing, so
        // whatever the driver refused it for is a reason of its own. Accepting the certificate anyway
        // would waive that reason, which is exactly what a blanket trust toggle does.
        var provider = new FakeSqlProvider { OpenFailure = new FakeDbException("The certificate chain was issued by an authority that is not trusted.") };
        using var connector = CreateConnector(provider, SoundCertificateReading(), trustedCertificate: null);

        var results = connector.ValidateSettingValues(CreateSettingValues(), _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Exception, Is.TypeOf<FakeDbException>());
            Assert.That(provider.BuiltConnectionSettings, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void ValidateSettingValues_DriverThatCannotBeToldToAcceptACertificate_ReportsTheFailureInstead()
    {
        // Oracle Database's driver is this case: it takes trust anchors from an Oracle wallet only, so
        // there is nothing JIM can hand it, and pretending otherwise would report trust it does not have.
        var provider = new FakeSqlProvider
        {
            CanPinServerCertificate = false,
            OpenFailure = new FakeDbException("ORA-28759: failure to open file")
        };
        using var connector = CreateConnector(provider, SoundCertificateReading(), trustedCertificate: _serverCertificate);

        var results = connector.ValidateSettingValues(CreateSettingValues(), _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].ErrorMessage, Does.Contain("ORA-28759"));
            Assert.That(provider.BuiltConnectionSettings, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void ValidateSettingValues_EncryptionDisabled_NeverLooksAtACertificateAtAll()
    {
        // ResolveSecureEndpoint answering null is the whole guard: without it, a failed connection would
        // have JIM open a TLS handshake against whatever host happened to be configured.
        var provider = new FakeSqlProvider { OpenFailure = new FakeDbException("Login timeout expired.") };
        using var connector = CreateConnector(provider, SoundCertificateReading(), trustedCertificate: _serverCertificate);
        var settingValues = CreateSettingValues();
        settingValues.Single(sv => sv.Setting.Name == SqlConnectorConstants.SettingSqlServerEncryptConnection).CheckboxValue = false;

        connector.ValidateSettingValues(settingValues, _logger);

        Assert.That(connector.ServerCertificateReads, Is.Zero);
    }

    [Test]
    public void ValidateSettingValues_OracleNativeNetworkEncryption_NeverLooksAtACertificateAtAll()
    {
        // Native Network Encryption is encrypted, but it is not TLS: the session is negotiated inside
        // Oracle Net and no certificate is ever presented. Treating "encrypted" as "has a certificate"
        // would have JIM probe a listener that does not speak TLS and report a certificate problem where
        // there is no certificate.
        var provider = new FakeSqlProvider { OpenFailure = new FakeDbException("ORA-12570: Network Session: Unexpected packet read error") };
        using var connector = CreateConnector(provider, SoundCertificateReading(), trustedCertificate: _serverCertificate);

        var results = connector.ValidateSettingValues(CreateOracleSettingValues(SqlConnectorConstants.OracleEncryptionNativeNetworkEncryption), _logger);

        Assert.Multiple(() =>
        {
            Assert.That(connector.ServerCertificateReads, Is.Zero);
            Assert.That(results[0].ErrorMessage, Does.Contain("ORA-12570"), "The driver's own account is what reaches the administrator.");
        });
    }

    [Test]
    public void ValidateSettingValues_OracleTcps_StillLooksAtTheCertificate()
    {
        // TCPS is genuinely TLS, so the diagnosis path is exactly as relevant as it is for an encrypted
        // Microsoft SQL Server connection.
        var provider = new FakeSqlProvider
        {
            CanPinServerCertificate = false,
            OpenFailure = new FakeDbException("ORA-29024: Certificate validation failure")
        };
        using var connector = CreateConnector(provider, UntrustedCertificateReading(), trustedCertificate: null);

        var results = connector.ValidateSettingValues(CreateOracleSettingValues(SqlConnectorConstants.OracleEncryptionTcps), _logger);

        Assert.Multiple(() =>
        {
            Assert.That(connector.ServerCertificateReads, Is.EqualTo(1));
            Assert.That(results[0].Exception, Is.TypeOf<ServerCertificateRejectedException>());
        });
    }

    [Test]
    public void Dispose_AfterAcceptingACertificate_RemovesTheTemporaryTrustFile()
    {
        var provider = new FakeSqlProvider { SucceedsOnlyWithAPinnedCertificate = true };
        var connector = CreateConnector(provider, SoundCertificateReading(), trustedCertificate: _serverCertificate);
        connector.ValidateSettingValues(CreateSettingValues(), _logger);
        var trustFilePath = provider.BuiltConnectionSettings[1].PinnedServerCertificatePath!;

        connector.Dispose();

        Assert.That(System.IO.File.Exists(trustFilePath), Is.False);
    }

    #region Helpers

    /// <summary>
    /// A Connector whose look at the server's certificate is scripted, so the trust decision is testable
    /// without standing up a TLS server.
    /// </summary>
    private sealed class CertificateReadingSqlConnector : SqlConnector
    {
        private readonly ServerCertificateReading _reading;

        internal CertificateReadingSqlConnector(ServerCertificateReading reading)
        {
            _reading = reading;
        }

        /// <summary>
        /// How many times the server's certificate was looked at, so a test can assert it was not.
        /// </summary>
        internal int ServerCertificateReads { get; private set; }

        internal override ServerCertificateReading? ReadServerCertificate(SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> trustedCertificates, ILogger logger)
        {
            ServerCertificateReads++;
            return _reading;
        }
    }

    /// <summary>
    /// Stands in for the JIM certificate store. A fresh copy is handed out per call, because the shared
    /// diagnosis path disposes what it is given.
    /// </summary>
    private sealed class FakeCertificateProvider : ICertificateProvider
    {
        private readonly byte[]? _certificate;

        internal FakeCertificateProvider(X509Certificate2? certificate)
        {
            _certificate = certificate?.Export(X509ContentType.Cert);
        }

        public Task<List<X509Certificate2>> GetTrustedCertificatesAsync()
        {
            return Task.FromResult(_certificate == null
                ? new List<X509Certificate2>()
                : [X509CertificateLoader.LoadCertificate(_certificate)]);
        }
    }

    private CertificateReadingSqlConnector CreateConnector(FakeSqlProvider provider, ServerCertificateReading reading, X509Certificate2? trustedCertificate)
    {
        var connector = new CertificateReadingSqlConnector(reading) { ProviderFactory = _ => provider };
        connector.SetCertificateProvider(new FakeCertificateProvider(trustedCertificate));
        return connector;
    }

    /// <summary>
    /// What the probe reports for a certificate that passes every check JIM makes of it.
    /// </summary>
    private ServerCertificateReading SoundCertificateReading() => CreateReading(ServerCertificateFailureReason.None);

    /// <summary>
    /// What the probe reports for a certificate neither the operating system nor JIM vouches for.
    /// </summary>
    private ServerCertificateReading UntrustedCertificateReading() => CreateReading(ServerCertificateFailureReason.UntrustedIssuer);

    private ServerCertificateReading CreateReading(ServerCertificateFailureReason failureReason)
    {
        return new ServerCertificateReading
        {
            Diagnostic = new ServerCertificateDiagnostic
            {
                Host = "db.example.com",
                Port = 1433,
                Subject = _serverCertificate.Subject,
                Issuer = _serverCertificate.Issuer,
                Thumbprint = _serverCertificate.Thumbprint,
                FailureReason = failureReason,
                Remediation = "Add this certificate to the JIM certificate store."
            },
            Chain = new PresentedServerCertificateChain
            {
                Host = "db.example.com",
                Port = 1433,
                ReadAt = DateTime.UtcNow,
                IsSelfSigned = true,
                Leaf = new PresentedServerCertificate
                {
                    Thumbprint = _serverCertificate.Thumbprint,
                    Subject = _serverCertificate.Subject,
                    Issuer = _serverCertificate.Issuer,
                    ValidFrom = _serverCertificate.NotBefore.ToUniversalTime(),
                    ValidTo = _serverCertificate.NotAfter.ToUniversalTime(),
                    Data = _serverCertificate.Export(X509ContentType.Cert)
                }
            }
        };
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Self-signed, and therefore its own trust anchor: the case an administrator meets when a
        // database server presents a certificate their own estate issued.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Exported and reloaded so no private key travels with it, matching what a server presents.
        return X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
    }

    /// <summary>
    /// A complete Microsoft SQL Server configuration with encryption enabled.
    /// </summary>
    private static List<ConnectedSystemSettingValue> CreateSettingValues()
    {
        return
        [
            Setting(SqlConnectorConstants.SettingDatabaseType, ConnectedSystemSettingType.DropDown, stringValue: SqlConnectorConstants.DatabaseTypeSqlServer),
            Setting(SqlConnectorConstants.SettingHost, ConnectedSystemSettingType.String, stringValue: "db.example.com"),
            Setting(SqlConnectorConstants.SettingPort, ConnectedSystemSettingType.Integer),
            Setting(SqlConnectorConstants.SettingDatabaseName, ConnectedSystemSettingType.String, stringValue: "HR"),
            Setting(SqlConnectorConstants.SettingUsername, ConnectedSystemSettingType.String, stringValue: "jim_sync"),
            Setting(SqlConnectorConstants.SettingPassword, ConnectedSystemSettingType.StringEncrypted, encryptedValue: "sup3rs3cret"),
            Setting(SqlConnectorConstants.SettingSqlServerEncryptConnection, ConnectedSystemSettingType.CheckBox, checkboxValue: true),
            Setting(SqlConnectorConstants.SettingConnectionTimeout, ConnectedSystemSettingType.Integer, intValue: 5),
            Setting(SqlConnectorConstants.SettingDatabaseTimeZone, ConnectedSystemSettingType.String, stringValue: SqlConnectorConstants.DefaultDatabaseTimeZone)
        ];
    }

    /// <summary>
    /// A complete Oracle Database configuration on the named encryption mode.
    /// </summary>
    private static List<ConnectedSystemSettingValue> CreateOracleSettingValues(string encryptionMode)
    {
        return
        [
            Setting(SqlConnectorConstants.SettingDatabaseType, ConnectedSystemSettingType.DropDown, stringValue: SqlConnectorConstants.DatabaseTypeOracle),
            Setting(SqlConnectorConstants.SettingHost, ConnectedSystemSettingType.String, stringValue: "hr.example.com"),
            Setting(SqlConnectorConstants.SettingPort, ConnectedSystemSettingType.Integer),
            Setting(SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy, ConnectedSystemSettingType.DropDown, stringValue: SqlConnectorConstants.OracleIdentifiedByServiceName),
            Setting(SqlConnectorConstants.SettingOracleServiceName, ConnectedSystemSettingType.String, stringValue: "HRPDB"),
            Setting(SqlConnectorConstants.SettingUsername, ConnectedSystemSettingType.String, stringValue: "jim_sync"),
            Setting(SqlConnectorConstants.SettingPassword, ConnectedSystemSettingType.StringEncrypted, encryptedValue: "sup3rs3cret"),
            Setting(SqlConnectorConstants.SettingOracleEncryption, ConnectedSystemSettingType.DropDown, stringValue: encryptionMode),
            Setting(SqlConnectorConstants.SettingConnectionTimeout, ConnectedSystemSettingType.Integer, intValue: 5),
            Setting(SqlConnectorConstants.SettingDatabaseTimeZone, ConnectedSystemSettingType.String, stringValue: SqlConnectorConstants.DefaultDatabaseTimeZone)
        ];
    }

    private static ConnectedSystemSettingValue Setting(
        string name,
        ConnectedSystemSettingType type,
        string? stringValue = null,
        string? encryptedValue = null,
        int? intValue = null,
        bool checkboxValue = false)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name, Type = type },
            StringValue = stringValue,
            StringEncryptedValue = encryptedValue,
            IntValue = intValue,
            CheckboxValue = checkboxValue
        };
    }

    #endregion
}
