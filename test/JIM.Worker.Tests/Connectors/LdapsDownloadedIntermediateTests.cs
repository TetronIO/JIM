// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Connectors;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Trusting the root of a chain whose intermediate CA the directory server does not send (#1938).
/// </summary>
/// <remarks>
/// <para>
/// JIM finds the missing intermediate at the Authority Information Access address in the server's certificate, but
/// the LDAP client never downloads anything, so the root alone cannot be linked to the server's certificate. The
/// trust action therefore stores the downloaded intermediate alongside the root (#1914). Unit tests cover each half;
/// only a real directory server shows that what the trust action stores is what the connection needs.
/// </para>
/// <para>
/// The server comes from <c>test/scripts/Start-LdapsCertificateTestServers.ps1</c>, which also writes the
/// intermediate and root to the directory this fixture serves at the addresses named in the certificates. The
/// fixture is ignored when those environment variables are absent.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresLdaps")]
public class LdapsDownloadedIntermediateTests
{
    private const int ConnectedSystemId = 7;

    private string _host = null!;
    private int _port;
    private string _username = null!;
    private string _password = null!;
    private string _aiaDirectory = null!;
    private int _aiaPort;
    private string _rootPath = null!;
    private string _storeDirectory = null!;
    private CertificateDownloadServer _downloads = null!;
    private Serilog.Core.Logger _logger = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _host = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_INTERMEDIATE_HOST") ?? string.Empty;
        var port = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_INTERMEDIATE_PORT");
        var aiaPort = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_AIA_PORT");
        _aiaDirectory = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_AIA_DIRECTORY") ?? string.Empty;
        if (string.IsNullOrEmpty(_host) || string.IsNullOrEmpty(port) || string.IsNullOrEmpty(aiaPort) || !Directory.Exists(_aiaDirectory))
            Assert.Ignore("JIM_TEST_LDAPS_INTERMEDIATE_HOST/PORT or JIM_TEST_LDAPS_AIA_PORT/DIRECTORY not set; skipping the downloaded intermediate test. See test/scripts/Start-LdapsCertificateTestServers.ps1.");

        _port = int.Parse(port!);
        _aiaPort = int.Parse(aiaPort!);
        _rootPath = Path.Combine(_aiaDirectory, "root-ca.crt");
        _username = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_USERNAME") ?? "cn=admin,dc=example,dc=org";
        _password = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_PASSWORD") ?? "adminpassword";
    }

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _downloads = CertificateDownloadServer.ServingDirectory(_aiaDirectory, _aiaPort);
        _storeDirectory = Directory.CreateTempSubdirectory("jim-ldaps-store-").FullName;
    }

    [TearDown]
    public void TearDown()
    {
        _downloads.Dispose();
        Directory.Delete(_storeDirectory, recursive: true);
        _logger.Dispose();
    }

    [Test]
    public void Read_ServerSendsOnlyItsOwnCertificate_DownloadsTheIssuingCaAndReachesTheRoot()
    {
        using var connector = new JIM.Connectors.LDAP.LdapConnector();
        var endpoint = connector.ResolveSecureEndpoint(SettingValues())!;

        var reading = new ServerCertificateReader().Read(endpoint, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reading!.Chain!.Intermediates.Single().Subject, Does.Contain("JIM LDAPS Test Issuing CA C1"));
            Assert.That(reading!.Chain!.Intermediates.Single().Source, Is.EqualTo(ServerCertificateChainElementSource.Downloaded),
                "The server must not send its intermediate, or this fixture proves nothing.");
            Assert.That(reading!.Chain!.Root!.Subject, Does.Contain("JIM LDAPS Test Root CA C"));
            Assert.That(reading!.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
        }
    }

    /// <summary>
    /// The control: without the intermediate, the root alone does not connect, because the LDAP client does not
    /// download issuers. If this ever connects, storing intermediates is no longer needed and the test above is moot.
    /// </summary>
    [Test]
    public void OpenImportConnection_WithOnlyTheRootInTheJimStore_IsRejected()
    {
        Assert.That(() => OpenConnection(_rootPath), Throws.TypeOf<ServerCertificateRejectedException>());
    }

    [Test]
    public async Task TrustServerCertificateAsync_WithTheRoot_StoresWhatTheConnectionNeedsAndItConnectsAsync()
    {
        var stored = new List<TrustedCertificate>();
        using var jim = CreateApplication(stored);
        var rootThumbprint = (await jim.Certificates.ReadServerCertificateAsync(ConnectedSystemId)).Diagnostic!.RootThumbprint;

        var result = await jim.Certificates.TrustServerCertificateAsync(ConnectedSystemId, rootThumbprint!, Administrator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(ServerCertificateTrustOutcome.Trusted), result.Message);
            Assert.That(result.Certificate!.Thumbprint, Is.EqualTo(rootThumbprint));
            Assert.That(result.StoredIntermediates.Select(c => c.Name), Is.EqualTo(new[] { "JIM LDAPS Test Issuing CA C1" }));
        }

        Assert.That(() => OpenConnection(WriteToStore(stored)), Throws.Nothing,
            "Exactly what the trust action stored must be enough for the LDAP connection.");
    }

    /// <summary>
    /// Who the trust is audited against, as an administrator in the portal would be.
    /// </summary>
    private static MetaverseObject Administrator()
    {
        var administrator = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "User" } };
        administrator.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Id = 1, Name = Constants.BuiltInAttributes.DisplayName },
            StringValue = "Administrator"
        });
        return administrator;
    }

    private void OpenConnection(params string[] trustedCertificatePaths)
        => LdapsTestConnections.OpenConnection(_host, _port, useSecureConnection: true, _username, _password, _logger, trustedCertificatePaths);

    private List<ConnectedSystemSettingValue> SettingValues()
        => LdapsTestConnections.BuildSettingValues(_host, _port, useSecureConnection: true, _username, _password);

    private string[] WriteToStore(List<TrustedCertificate> stored)
    {
        return stored.Select((certificate, index) =>
        {
            var path = Path.Combine(_storeDirectory, $"{index}.crt");
            File.WriteAllBytes(path, certificate.CertificateData!);
            return path;
        }).ToArray();
    }

    /// <summary>
    /// The application with only its repository faked: the certificate reader and the LDAP Connector that resolves
    /// the endpoint are the real ones. Whatever the trust action adds to the store is captured in
    /// <paramref name="stored"/>.
    /// </summary>
    private JimApplication CreateApplication(List<TrustedCertificate> stored)
    {
        var repository = new Mock<IRepository>();
        var certificates = new Mock<ITrustedCertificateRepository>();
        var activities = new Mock<IActivityRepository>();
        var connectedSystems = new Mock<IConnectedSystemRepository>();

        repository.Setup(r => r.TrustedCertificates).Returns(certificates.Object);
        repository.Setup(r => r.Activity).Returns(activities.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);

        activities.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activities.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        certificates.Setup(r => r.GetEnabledAsync()).ReturnsAsync(() => stored.ToList());
        certificates.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>()))
            .ReturnsAsync((string thumbprint) => stored.Any(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)));
        certificates.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>()))
            .ReturnsAsync((TrustedCertificate certificate) => { stored.Add(certificate); return certificate; });

        connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => new ConnectedSystem
            {
                Id = ConnectedSystemId,
                Name = "Directory",
                ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.LdapConnectorName },
                SettingValues = SettingValues()
            });

        return new JimApplication(repository.Object);
    }
}
