// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using Tetron.PowerShellRemoting;
using Tetron.PowerShellRemoting.Kerberos;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's Exchange settings check (#2021, PRD requirements 2 and 3), against a fake Exchange session: it
/// points Kerberos at the realm, signs in as the directory account, refuses the save when the sign-in fails, warns when
/// the account cannot run some of the commands JIM needs, learns the Exchange version and the sign-in it used, and
/// always closes the session.
/// </summary>
[TestFixture]
public class LdapExchangeSettingsCheckTests
{
    private static readonly LdapExchangeKerberosSignIn SignIn = new("svc-jim@PANOPLY.LOCAL", "PANOPLY.LOCAL", "dc1.panoply.local");
    private static readonly Uri Endpoint = new("http://ex1.panoply.local/PowerShell/");

    private readonly List<KerberosRealm> _realmsConfigured = [];
    private RemoteSessionOptions? _openedWith;
    private FakeSession _session = null!;
    private Exception? _openFailure;
    private ConnectedSystemSettingValue _serverSetting = null!;

    [SetUp]
    public void SetUp()
    {
        _realmsConfigured.Clear();
        _openedWith = null;
        _openFailure = null;
        _session = new FakeSession(LdapExchangeCommandCoverage.RequiredCommands, version: "Version 15.2 (Build 1544.4)");
        _serverSetting = new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = LdapConnectorConstants.SETTING_EXCHANGE_SERVER },
            StringValue = "ex1.panoply.local"
        };
    }

    [TearDown]
    public void TearDown() => _session.Dispose();

    [Test]
    public async Task CheckAsync_AccountCanRunEverything_FindsNothingAndLearnsTheVersionAndTheSignInAsync()
    {
        var result = await CheckAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results, Is.Empty);
            Assert.That(result.Facts.Select(f => (f.Name, f.Value)), Is.EqualTo(new[]
            {
                ("Exchange", "Version 15.2 (Build 1544.4)"),
                ("Exchange Sign-in", "svc-jim@PANOPLY.LOCAL, by Kerberos through dc1.panoply.local")
            }));
        }
    }

    [Test]
    public async Task CheckAsync_Always_PointsKerberosAtTheRealmThenSignsInAsTheDirectoryAccountToExchangesEndpointAsync()
    {
        await CheckAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_realmsConfigured.Single(), Is.EqualTo(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"])).Using(new RealmComparer()));
            Assert.That(_openedWith!.ConnectionUri, Is.EqualTo(Endpoint));
            Assert.That(_openedWith.ConfigurationName, Is.EqualTo("Microsoft.Exchange"));
            Assert.That(_openedWith.UserPrincipalName, Is.EqualTo("svc-jim@PANOPLY.LOCAL"));
            Assert.That(_session.CommandsAskedAbout, Is.EquivalentTo(LdapExchangeCommandCoverage.RequiredCommands));
            Assert.That(_session.VersionAskedOf, Is.EqualTo("ex1.panoply.local"), "the server the setting names");
        }
    }

    [Test]
    public async Task CheckAsync_SignInRefused_RefusesTheSaveOnTheExchangeServerSettingAsync()
    {
        _openFailure = new InvalidOperationException("Connecting failed.", new KerberosException("gss_init_sec_context", 0xD0000, 0x96C73A25, "Clock skew too great"));

        var result = await CheckAsync();

        var refusal = result.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal.IsValid, Is.False);
            Assert.That(refusal.SettingValue, Is.SameAs(_serverSetting));
            Assert.That(refusal.ErrorMessage, Does.Contain("five minutes"));
            Assert.That(result.Facts, Is.Empty);
        }
    }

    [Test]
    public async Task CheckAsync_SomeCommandsHidden_WarnsOnTheExchangeServerSettingAndStillLearnsFactsAsync()
    {
        _session.Dispose();
        _session = new FakeSession(LdapExchangeCommandCoverage.RequiredCommands.Except(["Enable-Mailbox"]).ToList(), version: "Version 15.2 (Build 1544.4)");

        var result = await CheckAsync();

        var warning = result.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.IsWarning, Is.True);
            Assert.That(warning.SettingValue, Is.SameAs(_serverSetting));
            Assert.That(warning.ErrorMessage, Does.Contain("Enable-Mailbox"));
            Assert.That(result.Facts, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task CheckAsync_VersionHiddenFromTheAccount_LearnsOnlyTheSignInAsync()
    {
        // Get-ExchangeServer needs a configuration role a least-privilege recipient account does not have.
        _session.Dispose();
        _session = new FakeSession(LdapExchangeCommandCoverage.RequiredCommands, version: null);

        var result = await CheckAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results, Is.Empty, "not knowing the version stops nothing");
            Assert.That(result.Facts.Select(f => f.Name), Is.EqualTo(new[] { "Exchange Sign-in" }));
        }
    }

    [Test]
    public async Task CheckAsync_CommandsCannotBeListed_WarnsThatItCouldNotTellAndClosesTheSessionAsync()
    {
        _session.ListingFailure = new InvalidOperationException("The term 'Get-Command' is not recognized.");

        var result = await CheckAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results.Single().IsWarning, Is.True);
            Assert.That(result.Results.Single().ErrorMessage, Does.Contain("could not tell").And.Contain("Get-Command"));
            Assert.That(_session.Disposed, Is.True);
        }
    }

    [Test]
    public async Task CheckAsync_Succeeds_ClosesTheSessionAsync()
    {
        await CheckAsync();

        Assert.That(_session.Disposed, Is.True);
    }

    [Test]
    public void CheckAsync_CancelledByTheCaller_StopsRatherThanReportingAFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _openFailure = new OperationCanceledException(cancellation.Token);

        Assert.That(async () => await CheckAsync(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    private Task<ConnectorSettingsCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var check = new LdapExchangeSettingsCheck(
            (options, _) =>
            {
                _openedWith = options;
                return _openFailure == null ? Task.FromResult<ILdapExchangeCheckSession>(_session) : Task.FromException<ILdapExchangeCheckSession>(_openFailure);
            },
            (realm, _) =>
            {
                _realmsConfigured.Add(realm);
                return Task.CompletedTask;
            });

        return check.CheckAsync(SignIn, Endpoint, "not-a-real-password", [], _serverSetting, new LoggerConfiguration().CreateLogger(), cancellationToken);
    }

    private sealed class FakeSession(IReadOnlyList<string> visibleCommands, string? version) : ILdapExchangeCheckSession
    {
        public IReadOnlyList<string>? CommandsAskedAbout { get; private set; }
        public string? VersionAskedOf { get; private set; }
        public Exception? ListingFailure { get; set; }
        public bool Disposed { get; private set; }

        public Task<IReadOnlySet<string>> GetVisibleCommandsAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
        {
            CommandsAskedAbout = names;
            return ListingFailure != null
                ? Task.FromException<IReadOnlySet<string>>(ListingFailure)
                : Task.FromResult<IReadOnlySet<string>>(names.Where(visibleCommands.Contains).ToHashSet());
        }

        public Task<string?> GetServerVersionAsync(string server, CancellationToken cancellationToken)
        {
            VersionAskedOf = server;
            return Task.FromResult(version);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class RealmComparer : IEqualityComparer<KerberosRealm>
    {
        public bool Equals(KerberosRealm? x, KerberosRealm? y) => x?.Name == y?.Name && (x?.Kdcs ?? []).SequenceEqual(y?.Kdcs ?? []);
        public int GetHashCode(KerberosRealm obj) => obj.Name.GetHashCode();
    }
}
