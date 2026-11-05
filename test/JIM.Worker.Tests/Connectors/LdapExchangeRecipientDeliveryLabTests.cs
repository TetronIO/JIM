// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using JIM.Connectors.LDAP;
using JIM.Models.Exchange;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;
using Tetron.PowerShellRemoting;
using Tetron.PowerShellRemoting.Kerberos;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector delivering Exchange changes to the Active Directory lab's real Exchange server (#2021, Phase 2):
/// the lab twin of <see cref="LdapExchangeRecipientDeliveryHarnessTests"/>, through the Connector's own sign-in, reads,
/// <c>Enable-*</c> and <c>Set-*</c> commands and failure classification, every command pinned to the lab's domain
/// controller.
/// </summary>
/// <remarks>
/// <para>
/// These change the lab. They create seven objects named <c>jimlab&lt;time&gt;&lt;letter&gt;</c> in each object type's
/// default container, make them recipients, and remove them at the end. So they run only when
/// <c>JIM_LAB_ALLOW_WRITES</c> is <c>true</c> and <c>JIM_LAB_SVC_JIM_PASSWORD</c> holds svc-jim's password, from the
/// lab's runner VM, with the lab owner's agreement. The lab's nightly revert removes anything a failed run leaves.
/// </para>
/// <para>
/// Each fixture starts life through Exchange (<c>New-Mailbox -Shared</c>, <c>New-MailContact</c>,
/// <c>New-DistributionGroup</c>) and is then disabled, which leaves a plain directory object, as an LDAP export would.
/// That needs only svc-jim's Recipient Management role, so it works whatever the account is delegated over in the
/// directory.
/// </para>
/// <para>
/// Each session's open and close times (UTC) and statistics are written to the test output. ex1's front end has logged
/// HTTP 500s for Pings at session close; the times let its IIS log be matched to each close.
/// </para>
/// <para>
/// Settings, each defaulting to the lab's own: <c>JIM_LAB_REALM</c> (PANOPLY.LOCAL), <c>JIM_LAB_KDC</c> and
/// <c>JIM_LAB_DOMAIN_CONTROLLER</c> (dc1.panoply.local), <c>JIM_LAB_EXCHANGE_HOST</c> (ex1.panoply.local),
/// <c>JIM_LAB_ACCOUNT</c> (svc-jim), <c>JIM_LAB_LDAP_USERNAME</c> (PANOPLY\svc-jim), and <c>JIM_LAB_DC_CA</c>, a PEM file
/// with the domain controller's CA, where the runner's own trust store does not already hold it.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresExchangeLabWrites")]
[NonParallelizable]
public class LdapExchangeRecipientDeliveryLabTests
{
    private readonly string _prefix = $"jimlab{DateTime.UtcNow:yyMMddHHmmss}";
    private readonly Dictionary<char, ExchangeRecipientTarget> _fixtures = [];
    private readonly List<(string Identity, string Kind)> _cleanup = [];
    private Runspace? _runspace;
    private string? _mailDomain;

    private static string Realm => Setting("JIM_LAB_REALM", "PANOPLY.LOCAL");

    private static string DomainController => Setting("JIM_LAB_DOMAIN_CONTROLLER", Setting("JIM_LAB_KDC", "dc1.panoply.local"));

    private static string ExchangeHost => Setting("JIM_LAB_EXCHANGE_HOST", "ex1.panoply.local");

    private static string Account => Setting("JIM_LAB_ACCOUNT", "svc-jim");

    private static string LdapUsername => Setting("JIM_LAB_LDAP_USERNAME", $@"{Realm.Split('.')[0]}\{Account}");

    private static string? Password => Environment.GetEnvironmentVariable("JIM_LAB_SVC_JIM_PASSWORD");

    private string Name(char letter) => $"{_prefix}{letter}";

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(Password))
            Assert.Ignore("Needs the AD lab: set JIM_LAB_SVC_JIM_PASSWORD to svc-jim's password, on the lab's runner VM.");
        if (!string.Equals(Environment.GetEnvironmentVariable("JIM_LAB_ALLOW_WRITES"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Ignore("Changes the lab: set JIM_LAB_ALLOW_WRITES=true, with the lab owner's agreement, to run it.");

        // The Connector configures Kerberos for the realm it signs in to; the fixtures' own session uses the same.
        await ExchangeKerberosConfiguration.Process.EnsureRealmAsync(new KerberosRealm(Realm, [Setting("JIM_LAB_KDC", "dc1.panoply.local")]));
        _runspace = await RemoteSessionFactory.OpenAsync(new RemoteSessionOptions
        {
            ConnectionUri = new Uri($"http://{ExchangeHost}/PowerShell/"),
            ConfigurationName = "Microsoft.Exchange",
            UserPrincipalName = $"{Account}@{Realm}",
            Password = Password!
        });
        TestContext.Out.WriteLine($"Fixtures are named {_prefix}*, every command pinned to {DomainController}.");

        // Users for a mailbox (a), a shared mailbox (b), a mail user (c), an update in a second session (f) and a missing
        // database (g); a contact (d); a group (e).
        foreach (var letter in "abcfg")
            await NewUserAsync(letter);
        await NewContactAsync('d');
        await NewGroupAsync('e');
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_runspace == null)
            return;

        // Newest first, each removed as whatever it is now (LabFixtureRemoval); a failure is reported, not thrown, so the
        // rest still go.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            var (identity, kind) = _cleanup[i];
            var (recipient, _, _) = await RunAsync(ps => ps.AddCommand("Get-Recipient").AddParameter("Identity", identity)
                .AddParameter("DomainController", DomainController));
            var details = recipient.Count == 1 ? recipient[0].Properties["RecipientTypeDetails"]?.Value?.ToString() : null;
            LabFixtureRemoval removal;
            try
            {
                removal = LabFixtureRemoval.For(kind, details);
            }
            catch (ArgumentException ex)
            {
                TestContext.Out.WriteLine($"Clean-up of {identity} skipped, so the nightly revert will remove it: {ex.Message}");
                continue;
            }

            var failure = removal.EnableFirst == null ? null : await TryRunAsync(ps =>
            {
                ps.AddCommand(removal.EnableFirst).AddParameter("Identity", identity).AddParameter("DomainController", DomainController);
                if (kind != "group")
                    ps.AddParameter("ExternalEmailAddress", $"{identity}-cleanup@partner.example");
            });
            failure ??= await TryRunAsync(ps => ps.AddCommand(removal.Remove).AddParameter("Identity", identity)
                .AddParameter("Confirm", false).AddParameter("DomainController", DomainController));
            if (failure != null)
                TestContext.Out.WriteLine($"Clean-up of {identity} failed, so the nightly revert will remove it: {failure}");
        }

        _runspace.Dispose();
    }

    [Test, Order(1)]
    public async Task ConvergeAsync_UserNotYetARecipient_BecomesAMailboxAndAReRunChangesNothingAsync()
    {
        var declared = Declared(ExchangeRecipientTypes.Mailbox) with { Alias = Name('a') };

        var results = await InOneSessionAsync("mailbox", (_fixtures['a'], declared), (_fixtures['a'], declared));
        var (first, second) = (results[0], results[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Delivered, Is.True, first.Message);
            Assert.That(first.ChangedAnything, Is.True);
            Assert.That(second.Delivered, Is.True, second.Message);
            Assert.That(second.ChangedAnything, Is.False, "requirement 9: a recipient already in its declared state changes nothing");
        }

        Assert.That(await RecipientTypeDetailsAsync(Name('a')), Is.EqualTo("UserMailbox"));
        _mailDomain = (await GetSingleAsync("Get-Recipient", Name('a'))).Properties["PrimarySmtpAddress"]?.Value?.ToString()?.Split('@').LastOrDefault();
    }

    [Test, Order(2)]
    public async Task ConvergeAsync_User_BecomesASharedMailboxAsync()
    {
        var result = (await InOneSessionAsync("shared mailbox", (_fixtures['b'], Declared(ExchangeRecipientTypes.SharedMailbox))))[0];

        Assert.That(result.Delivered, Is.True, result.Message);
        Assert.That(await RecipientTypeDetailsAsync(Name('b')), Is.EqualTo("SharedMailbox"));
    }

    [TestCase('c', ExchangeRecipientTypes.MailUser, "MailUser"), Order(3)]
    [TestCase('d', ExchangeRecipientTypes.MailContact, "MailContact")]
    public async Task ConvergeAsync_MailUserOrContact_IsEnabledWithItsExternalAddressAndAReRunChangesNothingAsync(char letter, string type, string expected)
    {
        var declared = Declared(type) with { ExternalAddress = $"{Name(letter)}@partner.example" };

        var results = await InOneSessionAsync(type, (_fixtures[letter], declared), (_fixtures[letter], declared));
        var (first, second) = (results[0], results[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Delivered, Is.True, first.Message);
            Assert.That(second.ChangedAnything, Is.False, "the external address read back with its SMTP: prefix is the one declared");
        }

        Assert.That(await RecipientTypeDetailsAsync(Name(letter)), Is.EqualTo(expected));
    }

    [Test, Order(4)]
    public async Task ConvergeAsync_Group_IsMailEnabledAsync()
    {
        var result = (await InOneSessionAsync("group", (_fixtures['e'], Declared(ExchangeRecipientTypes.MailEnabledGroup))))[0];

        Assert.That(result.Delivered, Is.True, result.Message);
        Assert.That(await RecipientTypeDetailsAsync(Name('e')), Does.StartWith("Mail"));
    }

    [Test, Order(5)]
    public async Task ConvergeAsync_NewAliasAndPrimaryAddress_AreSetInASecondSessionAndThePolicyDoesNotPutItsOwnBackAsync()
    {
        var enabled = (await InOneSessionAsync("update, first session", (_fixtures['f'], Declared(ExchangeRecipientTypes.Mailbox))))[0];
        Assert.That(enabled.Delivered, Is.True, enabled.Message);

        var domain = _mailDomain ?? (await GetSingleAsync("Get-Recipient", Name('f'))).Properties["PrimarySmtpAddress"]?.Value?.ToString()?.Split('@').Last();
        var alias = $"{Name('f')}renamed";
        var address = $"{alias}@{domain}";
        var updated = (await InOneSessionAsync("update, second session",
            (_fixtures['f'], Declared(ExchangeRecipientTypes.Mailbox) with { Alias = alias, PrimarySmtpAddress = address })))[0];

        var recipient = await GetSingleAsync("Get-Recipient", Name('f'));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Delivered, Is.True, updated.Message);
            Assert.That(updated.ChangedAnything, Is.True);
            Assert.That(recipient.Properties["Alias"]?.Value?.ToString(), Is.EqualTo(alias));
            Assert.That(recipient.Properties["PrimarySmtpAddress"]?.Value?.ToString(), Is.EqualTo(address).IgnoreCase,
                "a declared primary address turns the Email Address Policy off for the recipient, or the policy puts its own back");
        }
    }

    [Test, Order(6)]
    public async Task ConvergeAsync_TypeChangeOnAnExistingMailbox_IsRefusedNamingBothTypesAsync()
    {
        var result = (await InOneSessionAsync("type change", (_fixtures['a'], Declared(ExchangeRecipientTypes.SharedMailbox))))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Refused), result.Message);
            Assert.That(result.Message, Does.Contain("Mailbox").And.Contain("SharedMailbox"));
        }

        Assert.That(await RecipientTypeDetailsAsync(Name('a')), Is.EqualTo("UserMailbox"), "a refusal changes nothing");
    }

    [Test, Order(7)]
    public async Task ConvergeAsync_ObjectExchangeCannotFind_IsObjectNotFoundAsync()
    {
        var missing = _fixtures['a'] with
        {
            ObjectGuid = null,
            DistinguishedName = $"CN={_prefix}-missing,{_fixtures['a'].DistinguishedName!.Split(',', 2)[1]}"
        };

        var result = (await InOneSessionAsync("not found", (missing, Declared(ExchangeRecipientTypes.Mailbox))))[0];

        Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.ObjectNotFound), result.Message);
    }

    [Test, Order(8)]
    public async Task ConvergeAsync_DatabaseThatDoesNotExist_IsRefusedNamingItAndTheObjectStaysUsableAsync()
    {
        var database = $"{_prefix}-no-such-database";

        var results = await InOneSessionAsync("missing database",
            (_fixtures['g'], Declared(ExchangeRecipientTypes.Mailbox) with { Database = database }),
            (_fixtures['g'], Declared(ExchangeRecipientTypes.Mailbox)));
        var (refused, delivered) = (results[0], results[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Refused), refused.Message);
            Assert.That(refused.Message, Does.Contain(database));
            Assert.That(delivered.Delivered, Is.True, $"the same object, declared without the database, is delivered: {delivered.Message}");
        }
    }

    /// <summary>
    /// Opens a Connector session, converges each target on its declared state in turn, one at a time as the lane does,
    /// and closes it, writing the session's open and close times and statistics to the test output so ex1's logs can be
    /// matched to it.
    /// </summary>
    private async Task<List<ExchangeConvergeResult>> InOneSessionAsync(string label,
        params (ExchangeRecipientTarget Target, ExchangeDeclaredState Declared)[] steps)
    {
        var ca = Environment.GetEnvironmentVariable("JIM_LAB_DC_CA");
        var stopwatch = Stopwatch.StartNew();
        var opening = DateTime.UtcNow;
        var results = new List<ExchangeConvergeResult>();
        ExchangeSessionStatistics? statistics;
        using (var connector = new LdapConnector())
        {
            connector.SetCertificateProvider(new LdapsTestConnections.FakeCertificateProvider(string.IsNullOrWhiteSpace(ca) ? [] : [ca]));
            await connector.OpenExchangeSessionAsync(Settings(), CancellationToken.None);
            TestContext.Out.WriteLine($"Session '{label}': opened {opening:O} (UTC) in {stopwatch.ElapsedMilliseconds} ms.");

            foreach (var (target, declared) in steps)
                results.Add(await connector.ConvergeAsync(target, declared, CancellationToken.None));

            statistics = connector.GetSessionStatistics();
            TestContext.Out.WriteLine($"Session '{label}': closing {DateTime.UtcNow:O} (UTC).");
        }

        TestContext.Out.WriteLine($"Session '{label}': closed {DateTime.UtcNow:O} (UTC), {stopwatch.ElapsedMilliseconds} ms in all; {statistics}.");
        return results;
    }

    private async Task NewUserAsync(char letter)
    {
        // A shared mailbox needs no password; disabling it leaves the plain user an LDAP export would.
        await RunCleanAsync(ps => ps.AddCommand("New-Mailbox").AddParameter("Shared", true).AddParameter("Name", Name(letter))
            .AddParameter("Alias", Name(letter)).AddParameter("DomainController", DomainController));
        _cleanup.Add((Name(letter), "user"));
        await RunCleanAsync(ps => ps.AddCommand("Disable-Mailbox").AddParameter("Identity", Name(letter)).AddParameter("Confirm", false)
            .AddParameter("DomainController", DomainController));
        await AddFixtureAsync(letter, "Get-User", "user");
    }

    private async Task NewContactAsync(char letter)
    {
        await RunCleanAsync(ps => ps.AddCommand("New-MailContact").AddParameter("Name", Name(letter)).AddParameter("Alias", Name(letter))
            .AddParameter("ExternalEmailAddress", $"{Name(letter)}-fixture@partner.example").AddParameter("DomainController", DomainController));
        _cleanup.Add((Name(letter), "contact"));
        await RunCleanAsync(ps => ps.AddCommand("Disable-MailContact").AddParameter("Identity", Name(letter)).AddParameter("Confirm", false)
            .AddParameter("DomainController", DomainController));
        await AddFixtureAsync(letter, "Get-Contact", "contact");
    }

    private async Task NewGroupAsync(char letter)
    {
        // No -Type: Recipient Management's entry for New-DistributionGroup leaves it out (Exchange keeps it in Security
        // Group Creation and Membership), and without it the group is a universal distribution group, as wanted.
        await RunCleanAsync(ps => ps.AddCommand("New-DistributionGroup").AddParameter("Name", Name(letter)).AddParameter("Alias", Name(letter))
            .AddParameter("DomainController", DomainController));
        _cleanup.Add((Name(letter), "group"));
        await RunCleanAsync(ps => ps.AddCommand("Disable-DistributionGroup").AddParameter("Identity", Name(letter)).AddParameter("Confirm", false)
            .AddParameter("DomainController", DomainController));
        await AddFixtureAsync(letter, "Get-Group", "group");
    }

    /// <summary>Reads the plain object's GUID and DN back, so the Connector targets it as it would after an export.</summary>
    private async Task AddFixtureAsync(char letter, string readCommand, string objectType)
    {
        var read = await GetSingleAsync(readCommand, Name(letter));
        _fixtures[letter] = new ExchangeRecipientTarget
        {
            ConnectedSystemObjectId = Guid.NewGuid(),
            ObjectGuid = Guid.Parse(read.Properties["Guid"]!.Value!.ToString()!),
            DistinguishedName = read.Properties["DistinguishedName"]?.Value?.ToString(),
            ObjectType = objectType,
            DomainController = DomainController
        };
    }

    private List<ConnectedSystemSettingValue> Settings()
    {
        var settings = LdapsTestConnections.BuildSettingValues(DomainController, 636, useSecureConnection: true, LdapUsername, Password!);
        settings.Add(new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS, Type = ConnectedSystemSettingType.CheckBox },
            CheckboxValue = true
        });
        settings.Add(new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = LdapConnectorConstants.SETTING_EXCHANGE_SERVER, Type = ConnectedSystemSettingType.String },
            StringValue = ExchangeHost
        });
        return settings;
    }

    private static ExchangeDeclaredState Declared(string type) => new() { RecipientType = type };

    private async Task<string?> RecipientTypeDetailsAsync(string identity) =>
        (await GetSingleAsync("Get-Recipient", identity)).Properties["RecipientTypeDetails"]?.Value?.ToString();

    private async Task<PSObject> GetSingleAsync(string command, string identity)
    {
        var (output, errors, thrown) = await RunAsync(ps => ps.AddCommand(command).AddParameter("Identity", identity)
            .AddParameter("DomainController", DomainController));
        Assert.That(thrown == null && errors.Count == 0 && output.Count == 1,
            $"{command} {identity} did not return exactly one object: {thrown?.Message} {string.Join(" | ", errors)}");
        return output[0];
    }

    /// <summary>Runs a command, returning what went wrong, or null when nothing did.</summary>
    private async Task<string?> TryRunAsync(Action<PowerShell> build)
    {
        var (_, errors, thrown) = await RunAsync(build);
        return thrown?.Message ?? (errors.Count > 0 ? errors[0].ToString() : null);
    }

    private async Task RunCleanAsync(Action<PowerShell> build)
    {
        var (_, errors, thrown) = await RunAsync(build);
        if (thrown != null)
            Assert.Fail($"A fixture command threw {thrown.GetType().Name}: {thrown.Message}");
        Assert.That(errors, Is.Empty, $"A fixture command wrote errors: {string.Join(" | ", errors)}");
    }

    private async Task<(IReadOnlyList<PSObject> Output, IReadOnlyList<ErrorRecord> Errors, Exception? Thrown)> RunAsync(Action<PowerShell> build)
    {
        using var ps = PowerShell.Create(_runspace!);
        build(ps);
        try
        {
            var output = await ps.InvokeAsync();
            return (output.ToList(), ps.Streams.Error.ToList(), null);
        }
        catch (RuntimeException error)
        {
            return ([], ps.Streams.Error.ToList(), error);
        }
    }

    private static string Setting(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
