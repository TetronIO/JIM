// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Connectors.SCIM;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class ScimConnectorTests
{
    private ScimConnector _connector = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new ScimConnector();
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    #region IConnector members

    [Test]
    public void Name_ReturnsScim2ConnectorConstant()
    {
        Assert.That(_connector.Name, Is.EqualTo(ConnectorConstants.Scim2ConnectorName));
    }

    #endregion

    #region IConnectorCapabilities members

    [Test]
    public void Capabilities_MatchScimConnectorContract()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_connector.SupportsFullImport, Is.True, nameof(_connector.SupportsFullImport));
            Assert.That(_connector.SupportsDeltaImport, Is.True, nameof(_connector.SupportsDeltaImport));
            Assert.That(_connector.SupportsExport, Is.True, nameof(_connector.SupportsExport));
            Assert.That(_connector.SupportsPartitions, Is.False, nameof(_connector.SupportsPartitions));
            Assert.That(_connector.SupportsPartitionContainers, Is.False, nameof(_connector.SupportsPartitionContainers));
            Assert.That(_connector.SupportsSecondaryExternalId, Is.False, nameof(_connector.SupportsSecondaryExternalId));
            Assert.That(_connector.SupportsUserSelectedExternalId, Is.False, nameof(_connector.SupportsUserSelectedExternalId));
            Assert.That(_connector.SupportsUserSelectedAttributeTypes, Is.False, nameof(_connector.SupportsUserSelectedAttributeTypes));
            Assert.That(_connector.SupportsAutoConfirmExport, Is.False, nameof(_connector.SupportsAutoConfirmExport));
            Assert.That(_connector.SupportsParallelExport, Is.True, nameof(_connector.SupportsParallelExport));
            Assert.That(_connector.SupportsPaging, Is.True, nameof(_connector.SupportsPaging));
            Assert.That(_connector.SupportsFilePaths, Is.False, nameof(_connector.SupportsFilePaths));
        });
    }

    #endregion

    #region IConnectorSettings members

    [Test]
    public void GetSettings_SettingNames_AreUnique()
    {
        var names = _connector.GetSettings().Select(s => s.Name).ToList();

        Assert.That(names, Is.Unique);
    }

    [Test]
    public void GetSettings_BaseUrl_IsRequiredConnectivityString()
    {
        var setting = GetSetting(ScimConnectorConstants.SettingBaseUrl);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Required, Is.True);
            Assert.That(setting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Connectivity));
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.String));
        });
    }

    [Test]
    public void GetSettings_AuthenticationMethod_IsRequiredDropDownWithAllMethods()
    {
        var setting = GetSetting(ScimConnectorConstants.SettingAuthenticationMethod);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Required, Is.True);
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
            Assert.That(setting.DropDownValues, Is.EquivalentTo(new[]
            {
                ScimConnectorConstants.AuthMethodOAuthClientCredentials,
                ScimConnectorConstants.AuthMethodHttpBasic,
                ScimConnectorConstants.AuthMethodStaticBearerToken,
                ScimConnectorConstants.AuthMethodCustomHeader
            }));
            Assert.That(setting.DefaultStringValue, Is.EqualTo(ScimConnectorConstants.AuthMethodOAuthClientCredentials));
        });
    }

    [Test]
    public void GetSettings_SecretSettings_AreStringEncrypted()
    {
        var secretSettingNames = new[]
        {
            ScimConnectorConstants.SettingClientSecret,
            ScimConnectorConstants.SettingPassword,
            ScimConnectorConstants.SettingBearerToken,
            ScimConnectorConstants.SettingAuthenticationHeaderValue
        };

        Assert.Multiple(() =>
        {
            foreach (var name in secretSettingNames)
                Assert.That(GetSetting(name).Type, Is.EqualTo(ConnectedSystemSettingType.StringEncrypted), name);
        });
    }

    [Test]
    public void GetSettings_ConditionalSettings_AreRelevantOnlyForTheirAuthenticationMethod()
    {
        var expectations = new (string SettingName, string RequiredWhenValue)[]
        {
            (ScimConnectorConstants.SettingTokenEndpointUrl, ScimConnectorConstants.AuthMethodOAuthClientCredentials),
            (ScimConnectorConstants.SettingClientId, ScimConnectorConstants.AuthMethodOAuthClientCredentials),
            (ScimConnectorConstants.SettingClientSecret, ScimConnectorConstants.AuthMethodOAuthClientCredentials),
            (ScimConnectorConstants.SettingUsername, ScimConnectorConstants.AuthMethodHttpBasic),
            (ScimConnectorConstants.SettingPassword, ScimConnectorConstants.AuthMethodHttpBasic),
            (ScimConnectorConstants.SettingBearerToken, ScimConnectorConstants.AuthMethodStaticBearerToken),
            (ScimConnectorConstants.SettingAuthenticationHeaderName, ScimConnectorConstants.AuthMethodCustomHeader),
            (ScimConnectorConstants.SettingAuthenticationHeaderValue, ScimConnectorConstants.AuthMethodCustomHeader)
        };

        Assert.Multiple(() =>
        {
            foreach (var (settingName, requiredWhenValue) in expectations)
            {
                var setting = GetSetting(settingName);
                Assert.That(setting.RequiredWhenSetting, Is.EqualTo(ScimConnectorConstants.SettingAuthenticationMethod), settingName);
                Assert.That(setting.RequiredWhenValue, Is.EqualTo(requiredWhenValue), settingName);
            }
        });
    }

    [Test]
    public void GetSettings_CertificateValidation_DefaultsToFullValidation()
    {
        var setting = GetSetting(ScimConnectorConstants.SettingCertificateValidation);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
            Assert.That(setting.DropDownValues, Is.EquivalentTo(new[]
            {
                ScimConnectorConstants.CertValidationFull,
                ScimConnectorConstants.CertValidationSkip
            }));
            Assert.That(setting.DefaultStringValue, Is.EqualTo(ScimConnectorConstants.CertValidationFull));
        });
    }

    [Test]
    public void GetSettings_MinimumTlsVersion_DefaultsToTls12()
    {
        var setting = GetSetting(ScimConnectorConstants.SettingMinimumTlsVersion);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Type, Is.EqualTo(ConnectedSystemSettingType.DropDown));
            Assert.That(setting.DropDownValues, Is.EquivalentTo(new[]
            {
                ScimConnectorConstants.TlsVersion12,
                ScimConnectorConstants.TlsVersion13
            }));
            Assert.That(setting.DefaultStringValue, Is.EqualTo(ScimConnectorConstants.TlsVersion12));
        });
    }

    #endregion

    #region ValidateSettingValues

    [Test]
    public void ValidateSettingValues_EmptyBaseUrl_ReturnsNoResults()
    {
        // the generic ConnectorSettingValidator reports missing required values; the connector just short-circuits.
        var results = _connector.ValidateSettingValues(CreateSettingValues(baseUrl: null), _logger);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ValidateSettingValues_RelativeUri_ReturnsInvalid()
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues("scim/v2"), _logger);

        AssertSingleInvalidBaseUrlResult(results);
    }

    [Test]
    public void ValidateSettingValues_NonHttpScheme_ReturnsInvalid()
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues("ldap://directory.example.com/scim/v2"), _logger);

        AssertSingleInvalidBaseUrlResult(results);
    }

    [Test]
    public void ValidateSettingValues_HttpNonLoopbackUrl_ReturnsInvalid()
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues("http://scim.example.com/scim/v2"), _logger);

        AssertSingleInvalidBaseUrlResult(results);
    }

    [Test]
    public void ValidateSettingValues_HttpLoopbackUrl_ReturnsValid()
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues("http://localhost:8080/scim/v2"), _logger);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ValidateSettingValues_HttpsUrl_ReturnsValid()
    {
        var results = _connector.ValidateSettingValues(CreateSettingValues("https://scim.example.com/scim/v2"), _logger);

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

    private static List<ConnectedSystemSettingValue> CreateSettingValues(string? baseUrl)
    {
        return new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = ScimConnectorConstants.SettingBaseUrl },
                StringValue = baseUrl
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = ScimConnectorConstants.SettingAuthenticationMethod },
                StringValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials
            }
        };
    }

    private static void AssertSingleInvalidBaseUrlResult(List<ConnectorSettingValueValidationResult> results)
    {
        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsValid, Is.False);
            Assert.That(results[0].ErrorMessage, Is.Not.Null.And.Not.Empty);
            Assert.That(results[0].SettingValue?.Setting.Name, Is.EqualTo(ScimConnectorConstants.SettingBaseUrl));
        });
    }

    #endregion
}
