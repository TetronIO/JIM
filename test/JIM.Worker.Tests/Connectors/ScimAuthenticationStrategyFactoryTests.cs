// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Connectors.SCIM.Authentication;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Builds the right authentication strategy from a Connected System's settings, decrypting secrets on
/// the way through. This is the only place that reaches for <see cref="ICredentialProtection"/>, so the
/// strategies themselves never handle encrypted material.
/// </summary>
[TestFixture]
public class ScimAuthenticationStrategyFactoryTests
{
    private Mock<ICredentialProtection> _credentialProtection = null!;
    private HttpClient _tokenClient = null!;

    [SetUp]
    public void SetUp()
    {
        _credentialProtection = new Mock<ICredentialProtection>();
        // Stand in for real decryption: the factory must pass stored values through this, not use them raw.
        _credentialProtection.Setup(c => c.Unprotect(It.IsAny<string?>()))
            .Returns((string? value) => value == null ? null : value.Replace("encrypted:", string.Empty));
        _tokenClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
    }

    [TearDown]
    public void TearDown()
    {
        _tokenClient.Dispose();
    }

    private static ConnectedSystemSettingValue Setting(string name, string? stringValue = null, string? encryptedValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue,
            StringEncryptedValue = encryptedValue
        };
    }

    [Test]
    public void Create_OAuthClientCredentials_ReturnsOAuthStrategy()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodOAuthClientCredentials),
            Setting(ScimConnectorConstants.SettingTokenEndpointUrl, "https://provider.example.com/oauth2/token"),
            Setting(ScimConnectorConstants.SettingClientId, "scim-client"),
            Setting(ScimConnectorConstants.SettingClientSecret, encryptedValue: "encrypted:top-secret")
        };

        var strategy = ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient);

        Assert.Multiple(() =>
        {
            Assert.That(strategy, Is.InstanceOf<ScimOAuthClientCredentialsAuthentication>());
            _credentialProtection.Verify(c => c.Unprotect("encrypted:top-secret"), Times.Once);
        });
    }

    [Test]
    public void Create_HttpBasic_ReturnsBasicStrategyWithDecryptedPassword()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodHttpBasic),
            Setting(ScimConnectorConstants.SettingUsername, "scim-service"),
            Setting(ScimConnectorConstants.SettingPassword, encryptedValue: "encrypted:s3cr3t")
        };

        var strategy = ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient);

        Assert.Multiple(() =>
        {
            Assert.That(strategy, Is.InstanceOf<ScimBasicAuthentication>());
            _credentialProtection.Verify(c => c.Unprotect("encrypted:s3cr3t"), Times.Once);
        });
    }

    [Test]
    public void Create_StaticBearerToken_ReturnsBearerStrategy()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodStaticBearerToken),
            Setting(ScimConnectorConstants.SettingBearerToken, encryptedValue: "encrypted:token-value")
        };

        var strategy = ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient);

        Assert.That(strategy, Is.InstanceOf<ScimStaticBearerTokenAuthentication>());
    }

    [Test]
    public void Create_CustomHeader_ReturnsCustomHeaderStrategy()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodCustomHeader),
            Setting(ScimConnectorConstants.SettingAuthenticationHeaderName, "X-Api-Key"),
            Setting(ScimConnectorConstants.SettingAuthenticationHeaderValue, encryptedValue: "encrypted:api-key")
        };

        var strategy = ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient);

        Assert.That(strategy, Is.InstanceOf<ScimCustomHeaderAuthentication>());
    }

    [Test]
    public void Create_NoCredentialProtectionAvailable_UsesStoredValueAsIs()
    {
        // Mirrors the LDAP connector: an unencrypted stored value must still work, so a deployment that
        // predates credential protection is not locked out.
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodStaticBearerToken),
            Setting(ScimConnectorConstants.SettingBearerToken, encryptedValue: "plain-token")
        };

        var strategy = ScimAuthenticationStrategyFactory.Create(settings, credentialProtection: null, _tokenClient);

        Assert.That(strategy, Is.InstanceOf<ScimStaticBearerTokenAuthentication>());
    }

    [Test]
    public void Create_UnknownAuthenticationMethod_Throws()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, "Mutual TLS")
        };

        var exception = Assert.Throws<ScimAuthenticationException>(
            () => ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient));

        Assert.That(exception!.Message, Does.Contain("Mutual TLS"));
    }

    [Test]
    public void Create_MissingAuthenticationMethod_Throws()
    {
        var settings = new List<ConnectedSystemSettingValue>();

        Assert.Throws<ScimAuthenticationException>(
            () => ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient));
    }

    [Test]
    public void Create_OAuthMissingTokenEndpoint_ThrowsNamingTheSetting()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodOAuthClientCredentials),
            Setting(ScimConnectorConstants.SettingClientId, "scim-client"),
            Setting(ScimConnectorConstants.SettingClientSecret, encryptedValue: "encrypted:top-secret")
        };

        var exception = Assert.Throws<ScimAuthenticationException>(
            () => ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient));

        Assert.That(exception!.Message, Does.Contain(ScimConnectorConstants.SettingTokenEndpointUrl));
    }

    [Test]
    public void Create_OAuthTokenEndpointNotAValidUrl_ThrowsNamingTheSetting()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodOAuthClientCredentials),
            Setting(ScimConnectorConstants.SettingTokenEndpointUrl, "not-a-url"),
            Setting(ScimConnectorConstants.SettingClientId, "scim-client"),
            Setting(ScimConnectorConstants.SettingClientSecret, encryptedValue: "encrypted:top-secret")
        };

        var exception = Assert.Throws<ScimAuthenticationException>(
            () => ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient));

        Assert.That(exception!.Message, Does.Contain(ScimConnectorConstants.SettingTokenEndpointUrl));
    }

    [Test]
    public void Create_BasicMissingUsername_ThrowsNamingTheSetting()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodHttpBasic),
            Setting(ScimConnectorConstants.SettingPassword, encryptedValue: "encrypted:s3cr3t")
        };

        var exception = Assert.Throws<ScimAuthenticationException>(
            () => ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient));

        Assert.That(exception!.Message, Does.Contain(ScimConnectorConstants.SettingUsername));
    }

    [Test]
    public void Create_ExceptionMessages_NeverContainSecretValues()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodHttpBasic),
            Setting(ScimConnectorConstants.SettingPassword, encryptedValue: "encrypted:s3cr3t")
        };

        var exception = Assert.Throws<ScimAuthenticationException>(
            () => ScimAuthenticationStrategyFactory.Create(settings, _credentialProtection.Object, _tokenClient));

        Assert.That(exception!.ToString(), Does.Not.Contain("s3cr3t"));
    }
}
