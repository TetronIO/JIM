// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Connectors.Graph;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphConnectorTests
{
    private const string TenantGuid = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string ClientGuid = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5";

    private GraphConnector _connector = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        // Every validation that passes the shape checks goes on to a connectivity test, so the transport is stubbed.
        _connector = new StubbedTransportGraphConnector(StubbedTransportGraphConnector.HealthyTransport());
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    #region IConnector members

    [Test]
    public void Name_ReturnsGraphConnectorConstant()
    {
        Assert.That(_connector.Name, Is.EqualTo(ConnectorConstants.GraphConnectorName));
    }

    [Test]
    public void Description_IsSet()
    {
        Assert.That(_connector.Description, Is.Not.Null.And.Not.Empty);
    }

    #endregion

    #region IConnectorCapabilities members

    [Test]
    public void Capabilities_MatchTheDesignPlan()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.SupportsFullImport, Is.True, nameof(_connector.SupportsFullImport));
            Assert.That(_connector.SupportsDeltaImport, Is.True, nameof(_connector.SupportsDeltaImport));
            Assert.That(_connector.SupportsExport, Is.True, nameof(_connector.SupportsExport));
            Assert.That(_connector.SupportsPaging, Is.True, nameof(_connector.SupportsPaging));
            Assert.That(_connector.SupportsParallelExport, Is.False, nameof(_connector.SupportsParallelExport));
            Assert.That(_connector.SupportsPartitions, Is.False, nameof(_connector.SupportsPartitions));
            Assert.That(_connector.SupportsPartitionContainers, Is.False, nameof(_connector.SupportsPartitionContainers));
            Assert.That(_connector.SupportsUserSelectedExternalId, Is.False, nameof(_connector.SupportsUserSelectedExternalId));
            Assert.That(_connector.SupportsAutoConfirmExport, Is.False, nameof(_connector.SupportsAutoConfirmExport));
            Assert.That(_connector.SupportsFilePaths, Is.False, nameof(_connector.SupportsFilePaths));
        }
    }

    [Test]
    public void ExternalIdsUniqueAcrossObjectTypes_IsTrue()
    {
        // A directory object's id is unique across the tenant, whether the object is a user or a group.
        Assert.That(((IConnectorCapabilities)_connector).ExternalIdsUniqueAcrossObjectTypes, Is.True);
    }

    #endregion

    #region GetSettings

    [Test]
    public void GetSettings_SettingNames_AreUnique()
    {
        Assert.That(_connector.GetSettings().Select(s => s.Name), Is.Unique);
    }

    [TestCase(GraphConnectorConstants.SettingTenantId)]
    [TestCase(GraphConnectorConstants.SettingClientId)]
    public void GetSettings_IdentifierSetting_IsRequiredConnectivityString(string name)
    {
        var setting = GetSetting(name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.Required, Is.True);
            Assert.That(setting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Connectivity));
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.String));
        }
    }

    [Test]
    public void GetSettings_Cloud_IsDropDownOfEveryCloudDefaultingToGlobal()
    {
        var setting = GetSetting(GraphConnectorConstants.SettingCloud);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
            Assert.That(setting.DropDownValues, Is.EqualTo(GraphCloud.All.Select(c => c.Name)));
            Assert.That(setting.DefaultStringValue, Is.EqualTo(GraphConnectorConstants.CloudGlobal));
        }
    }

    [Test]
    public void GetSettings_AuthenticationMethod_IsRequiredDropDownDefaultingToCertificate()
    {
        var setting = GetSetting(GraphConnectorConstants.SettingAuthenticationMethod);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.Required, Is.True);
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
            Assert.That(setting.DropDownValues, Is.EqualTo(new[] { GraphConnectorConstants.AuthMethodCertificate, GraphConnectorConstants.AuthMethodFederatedCredential, GraphConnectorConstants.AuthMethodClientSecret }));
            Assert.That(setting.DefaultStringValue, Is.EqualTo(GraphConnectorConstants.AuthMethodCertificate), "A certificate, as Entra Connect now uses, is the default.");
        }
    }

    [TestCase(GraphConnectorConstants.SettingClientSecret)]
    [TestCase(GraphConnectorConstants.SettingCertificate)]
    [TestCase(GraphConnectorConstants.SettingCertificatePassword)]
    public void GetSettings_SecretSetting_IsStringEncrypted(string name)
    {
        Assert.That(GetSetting(name).Type, Is.EqualTo(ConnectedSystemSettingType.StringEncrypted));
    }

    [TestCase(GraphConnectorConstants.SettingClientSecret, GraphConnectorConstants.AuthMethodClientSecret)]
    [TestCase(GraphConnectorConstants.SettingCertificate, GraphConnectorConstants.AuthMethodCertificate)]
    public void GetSettings_Credential_IsRequiredOnlyForItsAuthenticationMethod(string name, string method)
    {
        var setting = GetSetting(name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.RequiredWhenSetting, Is.EqualTo(GraphConnectorConstants.SettingAuthenticationMethod));
            Assert.That(setting.RequiredWhenValue, Is.EqualTo(method));
        }
    }

    [TestCase(GraphConnectorConstants.SettingConnectionTimeout, GraphConnectorConstants.DefaultConnectionTimeoutSeconds)]
    [TestCase(GraphConnectorConstants.SettingMaximumWritesPerSecond, GraphConnectorConstants.DefaultMaximumWritesPerSecond)]
    [TestCase(GraphConnectorConstants.SettingMaxRetries, GraphConnectorConstants.DefaultMaxRetries)]
    public void GetSettings_NumericSetting_IsAnIntegerWithItsDefault(string name, int expectedDefault)
    {
        var setting = GetSetting(name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.Integer));
            Assert.That(setting.DefaultIntValue, Is.EqualTo(expectedDefault));
        }
    }

    [Test]
    public void GetSettings_CertificatePassword_IsOptional()
    {
        // A PFX may have no password, so the setting cannot be required; RequiredWhen would make it so.
        var setting = GetSetting(GraphConnectorConstants.SettingCertificatePassword);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.Required, Is.False);
            Assert.That(setting.RequiredWhenSetting, Is.Null);
        }
    }

    #endregion

    #region ValidateSettingValues

    [Test]
    public void ValidateSettingValues_ValidClientSecretConfiguration_ReturnsNoResults()
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues(), _logger);

        Assert.That(results, Is.Empty);
    }

    [TestCase(TenantGuid)]
    [TestCase("contoso.onmicrosoft.com")]
    [TestCase("corp.contoso.com")]
    [TestCase("Contoso.OnMicrosoft.com")]
    public void ValidateSettingValues_TenantIdIsGuidOrDomain_IsAccepted(string tenantId)
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues(tenantId: tenantId), _logger);

        Assert.That(results, Is.Empty);
    }

    [TestCase("common")]
    [TestCase("organizations")]
    [TestCase("consumers")]
    public void ValidateSettingValues_TenantIdIsMultiTenantAlias_IsRefusedNamingIt(string tenantId)
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues(tenantId: tenantId), _logger);

        AssertSingleError(results, GraphConnectorConstants.SettingTenantId, tenantId);
    }

    [TestCase("https://login.microsoftonline.com/contoso.onmicrosoft.com")]
    [TestCase("contoso")]
    [TestCase("not a tenant")]
    [TestCase("7a1f0c2e-3b4d-4e5f-8a9b")]
    public void ValidateSettingValues_TenantIdNeitherGuidNorDomain_IsRefused(string tenantId)
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues(tenantId: tenantId), _logger);

        AssertSingleError(results, GraphConnectorConstants.SettingTenantId, "domain");
    }

    [TestCase("my-app")]
    [TestCase("1c2d3e4f-5a6b-4c7d")]
    public void ValidateSettingValues_ClientIdNotAGuid_IsRefused(string clientId)
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues(clientId: clientId), _logger);

        AssertSingleError(results, GraphConnectorConstants.SettingClientId, "Application (client) ID");
    }

    [Test]
    public void ValidateSettingValues_MissingRequiredValues_LeavesThemToTheGenericValidator()
    {
        // ConnectorSettingValidator reports missing required values; reporting them again would show each twice.
        var results = _connector.ValidateSettingValues(CreateSettingValues(tenantId: null, clientId: null, clientSecret: null), _logger);

        Assert.That(results, Is.Empty);
    }

    [TestCase(GraphConnectorConstants.SettingConnectionTimeout, 0)]
    [TestCase(GraphConnectorConstants.SettingConnectionTimeout, GraphConnectorConstants.MaximumConnectionTimeoutSeconds + 1)]
    [TestCase(GraphConnectorConstants.SettingMaximumWritesPerSecond, 0)]
    [TestCase(GraphConnectorConstants.SettingMaximumWritesPerSecond, 21)]
    [TestCase(GraphConnectorConstants.SettingMaxRetries, -1)]
    [TestCase(GraphConnectorConstants.SettingMaxRetries, GraphConnectorConstants.MaximumMaxRetries + 1)]
    public void ValidateSettingValues_NumberOutOfRange_IsRefused(string name, int value)
    {
        var settings = CreateSettingValues();
        settings.Add(new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = name }, IntValue = value });

        var results = _connector.ValidateSettingValues(settings, _logger);

        AssertSingleError(results, name, name);
    }

    [Test]
    public void ValidateSettingValues_UnknownCloud_IsRefused()
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues(cloud: "Germany"), _logger);

        AssertSingleError(results, GraphConnectorConstants.SettingCloud, "Germany");
    }

    [Test]
    public void ValidateSettingValues_ValidCertificate_ReturnsNoResults()
    {
        var settings = CreateSettingValues(authenticationMethod: GraphConnectorConstants.AuthMethodCertificate, clientSecret: null,
            certificate: GraphTestCertificates.RsaPfx(), certificatePassword: GraphTestCertificates.Password);

        var results = _connector.ValidateSettingValues(settings, _logger);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ValidateSettingValues_UnusableCertificate_IsRefusedOnTheCertificateSetting()
    {
        var settings = CreateSettingValues(authenticationMethod: GraphConnectorConstants.AuthMethodCertificate, clientSecret: null,
            certificate: GraphTestCertificates.RsaPfx(), certificatePassword: "wrong-password");

        var results = _connector.ValidateSettingValues(settings, _logger);

        AssertSingleError(results, GraphConnectorConstants.SettingCertificate, GraphConnectorConstants.SettingCertificatePassword);
    }

    [Test]
    public void ValidateSettingValues_CertificateExpiringSoon_WarnsWithoutRefusing()
    {
        var pfx = GraphTestCertificates.RsaPfx(notAfter: DateTimeOffset.UtcNow.AddDays(10));
        var settings = CreateSettingValues(authenticationMethod: GraphConnectorConstants.AuthMethodCertificate, clientSecret: null,
            certificate: pfx, certificatePassword: GraphTestCertificates.Password);

        var results = _connector.ValidateSettingValues(settings, _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsWarning, Is.True);
            Assert.That(results[0].SettingValue?.Setting.Name, Is.EqualTo(GraphConnectorConstants.SettingCertificate));
        }
    }

    [Test]
    public void ValidateSettingValues_CertificateLeftOverWithClientSecretMethod_IsIgnored()
    {
        // A value left behind by switching Authentication Method is not in use, matching how RequiredWhen treats it.
        var settings = CreateSettingValues(certificate: "not a certificate");

        var results = _connector.ValidateSettingValues(settings, _logger);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ValidateSettingValues_EncryptedCertificate_IsDecryptedBeforeLoading()
    {
        var pfx = GraphTestCertificates.RsaPfx();
        var protection = new Mock<ICredentialProtection>();
        protection.Setup(p => p.Unprotect("protected-certificate")).Returns(pfx);
        protection.Setup(p => p.Unprotect("protected-password")).Returns(GraphTestCertificates.Password);
        _connector.SetCredentialProtection(protection.Object);
        var settings = CreateSettingValues(authenticationMethod: GraphConnectorConstants.AuthMethodCertificate, clientSecret: null,
            certificate: "protected-certificate", certificatePassword: "protected-password");

        var results = _connector.ValidateSettingValues(settings, _logger);

        Assert.That(results, Is.Empty);
    }

    #endregion

    #region helpers

    private ConnectorSetting GetSetting(string name)
    {
        var setting = _connector.GetSettings().SingleOrDefault(s => s.Name == name);
        Assert.That(setting, Is.Not.Null, $"Expected a setting named '{name}' to be defined.");
        return setting!;
    }

    private static List<ConnectedSystemSettingValue> CreateSettingValues(
        string? tenantId = TenantGuid,
        string? clientId = ClientGuid,
        string? cloud = GraphConnectorConstants.CloudGlobal,
        string authenticationMethod = GraphConnectorConstants.AuthMethodClientSecret,
        string? clientSecret = "client-secret-value",
        string? certificate = null,
        string? certificatePassword = null)
    {
        return
        [
            StringValue(GraphConnectorConstants.SettingTenantId, tenantId),
            StringValue(GraphConnectorConstants.SettingClientId, clientId),
            StringValue(GraphConnectorConstants.SettingCloud, cloud),
            StringValue(GraphConnectorConstants.SettingAuthenticationMethod, authenticationMethod),
            EncryptedValue(GraphConnectorConstants.SettingClientSecret, clientSecret),
            EncryptedValue(GraphConnectorConstants.SettingCertificate, certificate),
            EncryptedValue(GraphConnectorConstants.SettingCertificatePassword, certificatePassword)
        ];
    }

    private static ConnectedSystemSettingValue StringValue(string name, string? value) =>
        new() { Setting = new ConnectorDefinitionSetting { Name = name }, StringValue = value };

    private static ConnectedSystemSettingValue EncryptedValue(string name, string? value) =>
        new() { Setting = new ConnectorDefinitionSetting { Name = name }, StringEncryptedValue = value };

    private static void AssertSingleError(List<ConnectorSettingValueValidationResult> results, string settingName, string expectedInMessage)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results.Single().IsValid, Is.False);
            Assert.That(results.Single().SettingValue?.Setting.Name, Is.EqualTo(settingName));
            Assert.That(results.Single().ErrorMessage, Does.Contain(expectedInMessage).IgnoreCase);
        }
    }

    #endregion
}
