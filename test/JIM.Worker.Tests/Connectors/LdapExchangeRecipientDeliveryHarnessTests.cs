// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exchange;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector delivering Exchange changes end to end against the remoting harness (#2021, Phase 2b): a real
/// Kerberos sign-in through the harness's Samba domain controller, a real WS-Management session to the stand-in
/// Exchange endpoint, and the stand-in's recipients, which it keeps across sessions as Exchange keeps its own. Each test
/// uses identities of its own, so tests never meet each other's recipients.
/// </summary>
[TestFixture]
[Category("RequiresRemotingHarness")]
public class LdapExchangeRecipientDeliveryHarnessTests
{
    private const string DomainController = "dc1.harness.test";
    private const string ExchangeServer = "winrm-harness.harness.test:5985";

    private string _domainControllerCa = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var ca = Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_DC_CA");
        if (string.IsNullOrWhiteSpace(ca) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_WINRM")))
        {
            const string reason = "Needs the remoting harness: run Start-RemotingHarness.ps1 and export JIM_REMOTING_HARNESS_DC_CA and JIM_REMOTING_HARNESS_WINRM.";
            if (string.Equals(Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase))
                Assert.Fail(reason);
            Assert.Ignore(reason);
        }

        _domainControllerCa = ca!;
    }

    [Test]
    public async Task ConvergeAsync_UserNotYetARecipient_BecomesAMailboxAndAReRunChangesNothingAsync()
    {
        using var connector = await OpenAsync();
        var alice = User();
        var declared = Declared(ExchangeRecipientTypes.Mailbox) with { Alias = "alice", PrimarySmtpAddress = "alice.smith@harness.test" };

        var first = await connector.ConvergeAsync(alice, declared, CancellationToken.None);
        var second = await connector.ConvergeAsync(alice, declared, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Delivered, Is.True, first.Message);
            Assert.That(first.ChangedAnything, Is.True);
            Assert.That(second.Delivered, Is.True, second.Message);
            Assert.That(second.ChangedAnything, Is.False, "requirement 9: a recipient already in its declared state changes nothing");
            Assert.That(connector.GetSessionStatistics().Commands, Is.GreaterThan(0));
        }
    }

    [Test]
    public async Task ConvergeAsync_ExistingMailboxWithANewAliasAndAddress_IsUpdatedInASecondSessionAsync()
    {
        var alice = User();
        using (var connector = await OpenAsync())
            Assert.That((await connector.ConvergeAsync(alice, Declared(ExchangeRecipientTypes.SharedMailbox), CancellationToken.None)).Delivered, Is.True);

        using var later = await OpenAsync();
        var result = await later.ConvergeAsync(alice, Declared(ExchangeRecipientTypes.SharedMailbox) with { Alias = "sales", PrimarySmtpAddress = "sales@harness.test" },
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Delivered, Is.True, result.Message);
            Assert.That(result.ChangedAnything, Is.True);
        }
    }

    [TestCase("user", ExchangeRecipientTypes.MailUser)]
    [TestCase("contact", ExchangeRecipientTypes.MailContact)]
    public async Task ConvergeAsync_MailUserOrContact_IsEnabledWithItsExternalAddressAsync(string objectType, string type)
    {
        using var connector = await OpenAsync();
        var target = User() with { ObjectType = objectType };
        var declared = Declared(type) with { ExternalAddress = "alice@partner.example" };

        var first = await connector.ConvergeAsync(target, declared, CancellationToken.None);
        var second = await connector.ConvergeAsync(target, declared, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Delivered, Is.True, first.Message);
            Assert.That(second.ChangedAnything, Is.False, "the external address read back with its SMTP: prefix is the one declared");
        }
    }

    [Test]
    public async Task ConvergeAsync_Group_IsMailEnabledAsync()
    {
        using var connector = await OpenAsync();

        var result = await connector.ConvergeAsync(User() with { ObjectType = "group" }, Declared(ExchangeRecipientTypes.MailEnabledGroup), CancellationToken.None);

        Assert.That(result.Delivered, Is.True, result.Message);
    }

    [Test]
    public async Task ConvergeAsync_ObjectExchangeCannotFind_IsObjectNotFoundAsync()
    {
        using var connector = await OpenAsync();
        var missing = User() with { ObjectGuid = null, DistinguishedName = $"CN=missing-{Guid.NewGuid():N},CN=Users,DC=harness,DC=test" };

        var result = await connector.ConvergeAsync(missing, Declared(ExchangeRecipientTypes.Mailbox), CancellationToken.None);

        Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.ObjectNotFound), result.Message);
    }

    [Test]
    public async Task ConvergeAsync_DatabaseThatDoesNotExist_IsRefusedNamingItAsync()
    {
        // Requirement 20: a database that does not exist fails the step with a clear error.
        using var connector = await OpenAsync();

        var result = await connector.ConvergeAsync(User(), Declared(ExchangeRecipientTypes.Mailbox) with { Database = "DB99" }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Refused));
            Assert.That(result.Message, Does.Contain("DB99"));
        }
    }

    [Test]
    public async Task ConvergeAsync_TypeChangeOnAnExistingRecipient_IsRefusedAndChangesNothingAsync()
    {
        using var connector = await OpenAsync();
        var alice = User();
        Assert.That((await connector.ConvergeAsync(alice, Declared(ExchangeRecipientTypes.Mailbox), CancellationToken.None)).Delivered, Is.True);

        var result = await connector.ConvergeAsync(alice, Declared(ExchangeRecipientTypes.SharedMailbox), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Refused));
            Assert.That(result.Message, Does.Contain("Mailbox").And.Contain("SharedMailbox"));
        }
    }

    [Test]
    public async Task ConvergeAsync_AccountWhoseRolesHideEnableMailbox_IsInsufficientPermissionsAsync()
    {
        using var connector = await OpenAsync(@"HARNESS\svc-exchange-limited", "Limited@123!");

        var result = await connector.ConvergeAsync(User(), Declared(ExchangeRecipientTypes.Mailbox), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.InsufficientPermissions), result.Message);
            Assert.That(result.Message, Does.Contain("Enable-Mailbox").And.Contain("Recipient Management"));
        }
    }

    [Test]
    public void OpenExchangeSessionAsync_WrongPassword_ThrowsAuthenticationFailed()
    {
        using var connector = Connector();

        var ex = Assert.ThrowsAsync<ExchangeSessionException>(() =>
            connector.OpenExchangeSessionAsync(Settings(@"HARNESS\svc-winrm", "Wrong@123!", ExchangeServer), CancellationToken.None));

        Assert.That(ex!.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.AuthenticationFailed), ex.Message);
    }

    [Test]
    public void OpenExchangeSessionAsync_NothingListeningAtTheExchangeServer_ThrowsUnreachable()
    {
        using var connector = Connector();

        var ex = Assert.ThrowsAsync<ExchangeSessionException>(() =>
            connector.OpenExchangeSessionAsync(Settings(@"HARNESS\svc-winrm", "Client@123!", "winrm-harness.harness.test:5999"), CancellationToken.None));

        Assert.That(ex!.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable), ex.Message);
    }

    private async Task<LdapConnector> OpenAsync(string username = @"HARNESS\svc-winrm", string password = "Client@123!")
    {
        var connector = Connector();
        await connector.OpenExchangeSessionAsync(Settings(username, password, ExchangeServer), CancellationToken.None);
        return connector;
    }

    private LdapConnector Connector()
    {
        var connector = new LdapConnector();
        connector.SetCertificateProvider(new LdapsTestConnections.FakeCertificateProvider([_domainControllerCa]));
        return connector;
    }

    private static List<ConnectedSystemSettingValue> Settings(string username, string password, string exchangeServer)
    {
        var settings = LdapsTestConnections.BuildSettingValues(DomainController, 636, useSecureConnection: true, username, password);
        settings.Add(new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS, Type = ConnectedSystemSettingType.CheckBox },
            CheckboxValue = true
        });
        settings.Add(new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = LdapConnectorConstants.SETTING_EXCHANGE_SERVER, Type = ConnectedSystemSettingType.String },
            StringValue = exchangeServer
        });
        return settings;
    }

    private static ExchangeRecipientTarget User() => new()
    {
        ConnectedSystemObjectId = Guid.NewGuid(),
        ObjectGuid = Guid.NewGuid(),
        DistinguishedName = $"CN=harness-{Guid.NewGuid():N},CN=Users,DC=harness,DC=test",
        ObjectType = "user",
        DomainController = DomainController
    };

    private static ExchangeDeclaredState Declared(string type) => new() { RecipientType = type };
}
