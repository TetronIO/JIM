// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's Exchange settings check end to end (#2021, PRD requirements 2 and 3), against the remoting
/// harness: the directory read over LDAPS from the harness's Samba domain controller, Kerberos pointed at its realm, and
/// a sign-in to the stand-in Exchange endpoint, whose restricted session hides mailbox and server commands from
/// svc-exchange-limited as Exchange's role-based access control does.
/// </summary>
/// <remarks>
/// Needs the harness (<c>test/integration/remoting-harness/Start-RemotingHarness.ps1</c>), its variables, and this
/// host resolving <c>dc1.harness.test</c> and <c>winrm-harness.harness.test</c> to the harness containers. Skipped
/// without them, unless <c>JIM_REMOTING_HARNESS_REQUIRED</c> is true, when a missing harness fails instead.
/// </remarks>
[TestFixture]
[Category("RequiresRemotingHarness")]
public class LdapExchangeSettingsCheckHarnessTests
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

    [TestCase(@"HARNESS\svc-winrm")]
    [TestCase("svc-winrm@harness.test")]
    [TestCase("CN=svc-winrm,CN=Users,DC=harness,DC=test")]
    public async Task CheckSettingsAsync_AccountWithEveryRole_FindsNothingAndLearnsTheVersionAndTheSignInAsync(string username)
    {
        var result = await CheckAsync(username, "Client@123!");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results, Is.Empty, string.Join(" ", result.Results.Select(r => r.ErrorMessage)));
            Assert.That(result.Facts.Select(f => (f.Name, f.Value)), Is.EqualTo(new[]
            {
                ("Exchange", "Version 15.2 (Build 1544.4)"),
                ("Exchange Sign-in", "svc-winrm@HARNESS.TEST, by Kerberos through dc1.harness.test")
            }));
        }
    }

    [Test]
    public async Task CheckSettingsAsync_AccountWithoutMailboxRoles_WarnsNamingTheHiddenCommandsAndLearnsNoVersionAsync()
    {
        var result = await CheckAsync(@"HARNESS\svc-exchange-limited", "Limited@123!");

        var warning = result.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.IsWarning, Is.True, warning.ErrorMessage);
            Assert.That(warning.ErrorMessage, Does.Contain("Enable-Mailbox").And.Contain("Set-Mailbox").And.Contain("SharedMailbox"));
            Assert.That(warning.ErrorMessage, Does.Not.Contain("Enable-MailContact"));
            Assert.That(result.Facts.Select(f => f.Name), Is.EqualTo(new[] { "Exchange Sign-in" }), "Get-ExchangeServer is hidden from it too");
        }
    }

    [Test]
    public async Task CheckSettingsAsync_NothingListeningAtTheExchangeServer_RefusesNamingItAsync()
    {
        var result = await CheckAsync(@"HARNESS\svc-winrm", "Client@123!", exchangeServer: "winrm-harness.harness.test:5999");

        var refusal = result.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal.IsValid, Is.False);
            Assert.That(refusal.ErrorMessage, Does.Contain("could not reach Exchange at http://winrm-harness.harness.test:5999/PowerShell/"));
        }
    }

    /// <summary>
    /// Runs the check as the portal's save does, after the quick validation has bound to the directory.
    /// </summary>
    private async Task<ConnectorSettingsCheckResult> CheckAsync(string username, string password, string exchangeServer = ExchangeServer)
    {
        using var connector = new LdapConnector();
        connector.SetCertificateProvider(new LdapsTestConnections.FakeCertificateProvider([_domainControllerCa]));

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

        return await connector.CheckSettingsAsync(settings, new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger(), CancellationToken.None);
    }
}
