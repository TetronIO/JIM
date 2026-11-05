// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's Exchange settings (#2021, PRD requirement 1): Exchange recipients can be managed only on Active
/// Directory, so turning it on against any other directory is refused when the settings are saved.
/// </summary>
[TestFixture]
public class LdapConnectorExchangeSettingsTests
{
    // The directory type is internal to the Connector, so cases name it.
    [TestCase("SambaAD")]
    [TestCase("OpenLDAP")]
    [TestCase("DirectoryServer389")]
    [TestCase("Generic")]
    public void DirectoryTypeRefusal_ManagingExchangeOnAnotherDirectory_RefusesWithTheReason(string directoryType)
    {
        var refusal = LdapExchangeSettings.DirectoryTypeRefusal(manageExchangeRecipients: true, Enum.Parse<LdapDirectoryType>(directoryType));

        Assert.That(refusal, Does.Contain("Active Directory"));
    }

    [Test]
    public void DirectoryTypeRefusal_ManagingExchangeOnActiveDirectory_Allows()
    {
        Assert.That(LdapExchangeSettings.DirectoryTypeRefusal(manageExchangeRecipients: true, LdapDirectoryType.ActiveDirectory), Is.Null);
    }

    [TestCase("SambaAD")]
    [TestCase("ActiveDirectory")]
    public void DirectoryTypeRefusal_NotManagingExchange_NeverRefuses(string directoryType)
    {
        Assert.That(LdapExchangeSettings.DirectoryTypeRefusal(manageExchangeRecipients: false, Enum.Parse<LdapDirectoryType>(directoryType)), Is.Null);
    }

    [Test]
    public async Task CheckSettingsAsync_ExchangeNotManaged_ChecksNothingAndLearnsNothingAsync()
    {
        using var connector = new LdapConnector();

        var result = await connector.CheckSettingsAsync(Settings(manageExchange: false, exchangeServer: null), Logger(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results, Is.Empty);
            Assert.That(result.Facts, Is.Empty, "turning Exchange off clears what an earlier check learnt");
        }
    }

    [TestCase("ftp://ex1.panoply.local/")]
    [TestCase("ex1 panoply local")]
    public async Task CheckSettingsAsync_ExchangeServerUnusable_RefusesOnThatSettingBeforeReachingAnythingAsync(string exchangeServer)
    {
        using var connector = new LdapConnector();
        var settings = Settings(manageExchange: true, exchangeServer);

        var result = await connector.CheckSettingsAsync(settings, Logger(), CancellationToken.None);

        var refusal = result.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal.IsValid, Is.False);
            Assert.That(refusal.SettingValue!.Setting.Name, Is.EqualTo(LdapConnectorConstants.SETTING_EXCHANGE_SERVER));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void IsManagingExchangeRecipients_FollowsTheSetting(bool manageExchange)
    {
        using var connector = new LdapConnector();

        Assert.That(connector.IsManagingExchangeRecipients(Settings(manageExchange, "ex1.panoply.local")), Is.EqualTo(manageExchange));
    }

    [Test]
    public void OpenExchangeSessionAsync_ExchangeServerUnusable_IsRefusedSoItsChangesParkForAPerson()
    {
        using var connector = new LdapConnector();

        var ex = Assert.ThrowsAsync<JIM.Models.Exchange.ExchangeSessionException>(() =>
            connector.OpenExchangeSessionAsync(Settings(manageExchange: true, "ftp://ex1.panoply.local/"), CancellationToken.None));

        Assert.That(ex!.FailureKind, Is.EqualTo(JIM.Models.Transactional.ExchangeChangeFailureKind.Refused));
    }

    [Test]
    public void OpenExchangeSessionAsync_NoDirectoryAccount_IsRefusedNamingWhatIsMissing()
    {
        using var connector = new LdapConnector();

        var ex = Assert.ThrowsAsync<JIM.Models.Exchange.ExchangeSessionException>(() =>
            connector.OpenExchangeSessionAsync(Settings(manageExchange: true, "ex1.panoply.local"), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.FailureKind, Is.EqualTo(JIM.Models.Transactional.ExchangeChangeFailureKind.Refused));
            Assert.That(ex.Message, Does.Contain("Username and Password"));
        }
    }

    [Test]
    public void SessionMembers_NoSessionOpen_AreSafe()
    {
        using var connector = new LdapConnector();

        connector.CloseExchangeSession();

        Assert.That(connector.GetSessionStatistics(), Is.EqualTo(JIM.Models.Exchange.ExchangeSessionStatistics.None));
    }

    private static List<ConnectedSystemSettingValue> Settings(bool manageExchange, string? exchangeServer) =>
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS, Type = ConnectedSystemSettingType.CheckBox }, CheckboxValue = manageExchange },
        new() { Setting = new ConnectorDefinitionSetting { Name = LdapConnectorConstants.SETTING_EXCHANGE_SERVER, Type = ConnectedSystemSettingType.String }, StringValue = exchangeServer }
    ];

    private static ILogger Logger() => new LoggerConfiguration().CreateLogger();
}
