// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Connectors;
using JIM.Connectors.SCIM;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Security;
using JIM.Models.Staging;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A Connected System's encrypted settings (a bind password, a client secret) are encrypted by the application layer
/// whichever surface saved them.
/// </summary>
/// <remarks>
/// The portal binds what an administrator types straight to <see cref="ConnectedSystemSettingValue.StringEncryptedValue"/>
/// and saves through <see cref="JIM.Application.Servers.ConnectedSystemServer"/>; only the REST controller used to
/// encrypt, so every credential entered in the portal was stored in clear. These tests pin the encryption to the server,
/// where every surface passes through.
/// </remarks>
[TestFixture]
public class ConnectedSystemCredentialEncryptionTests
{
    private const string PlainPassword = "Correct-Horse-Battery-Staple-1";

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private CredentialProtectionService _credentialProtection = null!;
    private JimApplication _jim = null!;

    /// <summary>
    /// The setting values as they reached the repository, copied at the moment of the write, so an assertion sees what
    /// was persisted rather than whatever the object holds afterwards.
    /// </summary>
    private List<string?> _persistedEncryptedValues = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);

        // a Connector that connects over TLS asks for JIM's trusted certificates first; there are none here
        var certificateRepo = new Mock<ITrustedCertificateRepository>();
        certificateRepo.Setup(r => r.GetEnabledAsync()).ReturnsAsync([]);
        _repo.Setup(r => r.TrustedCertificates).Returns(certificateRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _persistedEncryptedValues = [];
        _csRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Callback<ConnectedSystem>(cs => _persistedEncryptedValues.AddRange(EncryptedValues(cs)))
            .Returns(Task.CompletedTask);

        // Configuration change capture is off: this fixture is about what is written, and a snapshot reload would need
        // a graph these mocks do not serve.
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                ValueType = ServiceSettingValueType.Boolean,
                Value = "false"
            });

        _credentialProtection = new CredentialProtectionService(new EphemeralDataProtectionProvider());
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _credentialProtection };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task UpdateConnectedSystemAsync_PortalSaveWithPlainCredential_PersistsCiphertextAsync()
    {
        // Exactly what the portal's Settings tab hands over: the typed password, bound straight to the encrypted field.
        var connectedSystem = BuildConnectedSystem(PlainPassword);

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, Administrator());

        AssertPersistedAsCiphertextOf(PlainPassword);
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_ApiKeySaveWithPlainCredential_PersistsCiphertextAsync()
    {
        var connectedSystem = BuildConnectedSystem(PlainPassword);

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, ApiKey());

        AssertPersistedAsCiphertextOf(PlainPassword);
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_AlreadyProtectedCredential_IsNotEncryptedAgainAsync()
    {
        // An untouched password field posts back the ciphertext it was loaded with; encrypting that again would store a
        // credential the Connector can never use.
        var stored = _credentialProtection.Protect(PlainPassword);
        var connectedSystem = BuildConnectedSystem(stored);

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, Administrator());

        Assert.That(_persistedEncryptedValues, Is.EqualTo(new[] { stored }));
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_NoCredentialEntered_LeavesTheSettingEmptyAsync()
    {
        var connectedSystem = BuildConnectedSystem(null);

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, Administrator());

        Assert.That(_persistedEncryptedValues, Is.EqualTo(new string?[] { null }));
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_PlainSetting_IsNotEncryptedAsync()
    {
        var connectedSystem = BuildConnectedSystem(PlainPassword);

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, Administrator());

        var host = connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Host");
        Assert.That(host.StringValue, Is.EqualTo("directory.example.org"));
    }

    [Test]
    public async Task ProtectStoredCredentialsAsync_PlainCredentialStored_EncryptsItInPlaceAsync()
    {
        // A credential saved from the portal before the fix: plain text in the encrypted column.
        var plain = new ConnectedSystemSettingValue { Id = 21, StringEncryptedValue = PlainPassword, Setting = EncryptedSetting() };
        var alreadyProtected = new ConnectedSystemSettingValue { Id = 22, StringEncryptedValue = _credentialProtection.Protect("already"), Setting = EncryptedSetting() };
        _csRepo.Setup(r => r.GetEncryptedSettingValuesAsync()).ReturnsAsync([plain, alreadyProtected]);

        var written = new Dictionary<int, string>();
        _csRepo.Setup(r => r.UpdateSettingValueEncryptedValueAsync(It.IsAny<int>(), It.IsAny<string>()))
            .Callback<int, string>((id, value) => written[id] = value)
            .Returns(Task.CompletedTask);

        var converged = await _jim.ConnectedSystems.ProtectStoredCredentialsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(converged, Is.EqualTo(1));
            Assert.That(written.Keys, Is.EquivalentTo(new[] { 21 }), "only the plain-text value is rewritten");
            Assert.That(_credentialProtection.IsProtected(written[21]), Is.True);
            Assert.That(_credentialProtection.Unprotect(written[21]), Is.EqualTo(PlainPassword));
        }
    }

    [Test]
    public async Task ProtectStoredCredentialsAsync_NothingStoredInClear_WritesNothingAsync()
    {
        var alreadyProtected = new ConnectedSystemSettingValue { Id = 22, StringEncryptedValue = _credentialProtection.Protect("already"), Setting = EncryptedSetting() };
        _csRepo.Setup(r => r.GetEncryptedSettingValuesAsync()).ReturnsAsync([alreadyProtected]);

        var converged = await _jim.ConnectedSystems.ProtectStoredCredentialsAsync();

        Assert.That(converged, Is.Zero);
        _csRepo.Verify(r => r.UpdateSettingValueEncryptedValueAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void ValidateConnectedSystemSettings_PlainCredentialTyped_ConnectorAcceptsItEncryptedAsync()
    {
        // The portal validates (and the Connector live-tests) before saving, with the typed password still in clear.
        // The SCIM Connector decrypts its credential before connecting; given the plain value it would refuse it.
        // Port 1 on loopback refuses the connection at once, so the test ends at a connectivity failure, not a hang.
        var connectedSystem = new ConnectedSystem
        {
            Id = 2,
            Name = "Service Provider",
            ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.ScimClientConnectorName },
            SettingValues =
            [
                ScimSetting(200, ScimConnectorConstants.SettingBaseUrl, ConnectedSystemSettingType.String, "http://127.0.0.1:1/scim/v2"),
                ScimSetting(201, ScimConnectorConstants.SettingAuthenticationMethod, ConnectedSystemSettingType.DropDown, ScimConnectorConstants.AuthMethodHttpBasic),
                ScimSetting(202, ScimConnectorConstants.SettingUsername, ConnectedSystemSettingType.String, "svc-jim"),
                new ConnectedSystemSettingValue
                {
                    Id = 23,
                    Setting = new ConnectorDefinitionSetting { Id = 203, Name = ScimConnectorConstants.SettingPassword, Type = ConnectedSystemSettingType.StringEncrypted },
                    StringEncryptedValue = PlainPassword
                }
            ]
        };

        IList<ConnectorSettingValueValidationResult> results = [];
        Assert.That(() => results = _jim.ConnectedSystems.ValidateConnectedSystemSettings(connectedSystem), Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Any(r => !r.IsValid && r.ErrorMessage != null && r.ErrorMessage.Contains("Could not connect")), Is.True,
                "validation should reach the connection attempt, which means the Connector accepted the credential");
            var credential = connectedSystem.SettingValues.Single(sv => sv.Id == 23).StringEncryptedValue;
            Assert.That(_credentialProtection.IsProtected(credential), Is.True, "the Connector was handed the credential in clear");
            Assert.That(_credentialProtection.Unprotect(credential), Is.EqualTo(PlainPassword));
        }
    }

    private static ConnectedSystemSettingValue ScimSetting(int settingId, string name, ConnectedSystemSettingType type, string value) => new()
    {
        Id = settingId - 180,
        Setting = new ConnectorDefinitionSetting { Id = settingId, Name = name, Type = type },
        StringValue = value
    };

    private void AssertPersistedAsCiphertextOf(string expectedPlainText)
    {
        Assert.That(_persistedEncryptedValues, Has.Count.EqualTo(1));
        var persisted = _persistedEncryptedValues[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted, Is.Not.EqualTo(expectedPlainText), "the credential reached the database in clear");
            Assert.That(_credentialProtection.IsProtected(persisted), Is.True);
            Assert.That(_credentialProtection.Unprotect(persisted), Is.EqualTo(expectedPlainText));
        }
    }

    private static IEnumerable<string?> EncryptedValues(ConnectedSystem connectedSystem) =>
        connectedSystem.SettingValues
            .Where(sv => sv.Setting.Type == ConnectedSystemSettingType.StringEncrypted)
            .Select(sv => sv.StringEncryptedValue);

    private static MetaverseObject Administrator() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Test Admin" };

    private static ApiKey ApiKey() => new() { Id = Guid.NewGuid(), Name = "Automation", KeyHash = "hash", KeyPrefix = "jim_ak_test" };

    private static ConnectorDefinitionSetting EncryptedSetting() =>
        new() { Id = 101, Name = "Password", Type = ConnectedSystemSettingType.StringEncrypted };

    private static ConnectedSystem BuildConnectedSystem(string? password) => new()
    {
        Id = 1,
        Name = "Corporate Directory",
        ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName },
        SettingValues =
        [
            new ConnectedSystemSettingValue
            {
                Id = 10,
                Setting = new ConnectorDefinitionSetting { Id = 100, Name = "Host", Type = ConnectedSystemSettingType.String },
                StringValue = "directory.example.org"
            },
            new ConnectedSystemSettingValue
            {
                Id = 11,
                Setting = EncryptedSetting(),
                StringEncryptedValue = password
            }
        ]
    };
}
