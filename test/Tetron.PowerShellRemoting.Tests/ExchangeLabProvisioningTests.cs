// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Tetron.PowerShellRemoting.Tests.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// The spike's Phase 3 (#2021): the cmdlets JIM's Exchange step runs, against the lab's real Exchange server, each pinned
/// to a domain controller with <c>-DomainController</c>, with Exchange's failure records and timings captured.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ExchangeLabTests"/>, these change the lab: they create five recipients named <c>jimspike&lt;time&gt;</c>
/// (two shared mailboxes, a mail user, a mail contact and a distribution group, in each object type's default container),
/// convert them, and remove them all again at the end. So they run only when <c>JIM_LAB_ALLOW_WRITES</c> is <c>true</c>
/// as well as the lab being configured, and only from the lab's runner VM with the lab owner's agreement. The lab's
/// nightly revert removes anything a failed run leaves behind.
/// </para>
/// <para>
/// Every recipient starts life through Exchange (<c>New-Mailbox -Shared</c> and its siblings) and is then disabled, so the
/// enable cmdlets JIM uses act on a plain directory object, as they will after an LDAP export. No password is sent:
/// shared mailboxes need none. The steps depend on one another and run in order.
/// </para>
/// </remarks>
[Category("RequiresExchangeLabWrites")]
[NonParallelizable]
public class ExchangeLabProvisioningTests
{
    private readonly string _prefix = $"jimspike{DateTime.UtcNow:yyMMddHHmmss}";
    private readonly List<(string Command, string Identity)> _cleanup = [];
    private readonly List<(string Step, long Milliseconds)> _timings = [];
    private Runspace? _runspace;

    private string SharedA => $"{_prefix}a";

    private string SharedB => $"{_prefix}b";

    private string MailUserC => $"{_prefix}c";

    private string ContactD => $"{_prefix}d";

    private string GroupE => $"{_prefix}e";

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        ExchangeLab.RequireLab();
        if (!string.Equals(Environment.GetEnvironmentVariable("JIM_LAB_ALLOW_WRITES"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Ignore("Changes the lab: set JIM_LAB_ALLOW_WRITES=true, with the lab owner's agreement, to run it.");

        var stopwatch = Stopwatch.StartNew();
        _runspace = await RemoteSessionFactory.OpenAsync(ExchangeLab.Options());
        TestContext.Out.WriteLine($"Opened {ExchangeLab.Options().ConnectionUri} as {ExchangeLab.Account} in {stopwatch.ElapsedMilliseconds} ms; " +
                                  $"objects are named {_prefix}*, every command pinned to {DomainController}.");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_runspace == null)
            return;

        // Remove what this run created, newest first; a failure here is reported, not thrown, so the rest still go.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            var (command, identity) = _cleanup[i];
            var (_, errors, _) = await RunAsync($"cleanup {command}", ps => ps.AddCommand(command)
                .AddParameter("Identity", identity).AddParameter("Confirm", false).AddParameter("DomainController", DomainController));
            if (errors.Count > 0)
                TestContext.Out.WriteLine($"Cleanup of {identity} failed: {errors[0]}");
        }

        TestContext.Out.WriteLine("Timings (ms): " + string.Join("; ", _timings.Select(t => $"{t.Step}={t.Milliseconds}")));
        TestContext.Out.WriteLine($"Session statistics: {RemoteSessionFactory.GetStatistics(_runspace)}");
        _runspace.Dispose();
    }

    private static string DomainController => Environment.GetEnvironmentVariable("JIM_LAB_DOMAIN_CONTROLLER") is { Length: > 0 } dc ? dc : ExchangeLab.Kdc;

    [Test, Order(1)]
    public async Task NewMailbox_Shared_CreatesASharedMailboxAsync()
    {
        await RunCleanAsync("New-Mailbox -Shared", ps => ps.AddCommand("New-Mailbox").AddParameter("Shared", true)
            .AddParameter("Name", SharedA).AddParameter("Alias", SharedA).AddParameter("DomainController", DomainController));
        _cleanup.Add(("Remove-Mailbox", SharedA));

        Assert.That(await RecipientTypeDetailsAsync(SharedA), Is.EqualTo("SharedMailbox"));
    }

    [Test, Order(2)]
    public async Task DisableMailboxThenEnableMailbox_LeavesAUserMailboxWhereExchangeChoseAsync()
    {
        await RunCleanAsync("Disable-Mailbox", ps => ps.AddCommand("Disable-Mailbox").AddParameter("Identity", SharedA)
            .AddParameter("Confirm", false).AddParameter("DomainController", DomainController));
        await RunCleanAsync("Enable-Mailbox", ps => ps.AddCommand("Enable-Mailbox").AddParameter("Identity", SharedA)
            .AddParameter("DomainController", DomainController));

        var mailbox = await GetSingleAsync("Get-Mailbox", SharedA);
        TestContext.Out.WriteLine($"{SharedA} placed by Exchange in {mailbox.Properties["Database"]?.Value}.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mailbox.Properties["RecipientTypeDetails"]?.Value?.ToString(), Is.EqualTo("UserMailbox"));
            Assert.That(mailbox.Properties["Database"]?.Value?.ToString(), Is.Not.EqualTo("MDB02"), "MDB02 is excluded from automatic distribution.");
        }
    }

    [Test, Order(3)]
    public async Task SetMailboxTypeShared_ConvertsAUserMailboxAsync()
    {
        await RunCleanAsync("Set-Mailbox -Type Shared", ps => ps.AddCommand("Set-Mailbox").AddParameter("Identity", SharedA)
            .AddParameter("Type", "Shared").AddParameter("DomainController", DomainController));

        Assert.That(await RecipientTypeDetailsAsync(SharedA), Is.EqualTo("SharedMailbox"));
    }

    [Test, Order(4)]
    public async Task EnableMailbox_WithDatabase_UsesThatDatabaseAsync()
    {
        await RunCleanAsync("New-Mailbox -Shared (b)", ps => ps.AddCommand("New-Mailbox").AddParameter("Shared", true)
            .AddParameter("Name", SharedB).AddParameter("Alias", SharedB).AddParameter("DomainController", DomainController));
        _cleanup.Add(("Remove-Mailbox", SharedB));
        await RunCleanAsync("Disable-Mailbox (b)", ps => ps.AddCommand("Disable-Mailbox").AddParameter("Identity", SharedB)
            .AddParameter("Confirm", false).AddParameter("DomainController", DomainController));
        await RunCleanAsync("Enable-Mailbox -Database", ps => ps.AddCommand("Enable-Mailbox").AddParameter("Identity", SharedB)
            .AddParameter("Database", "MDB02").AddParameter("DomainController", DomainController));

        var mailbox = await GetSingleAsync("Get-Mailbox", SharedB);
        Assert.That(mailbox.Properties["Database"]?.Value?.ToString(), Is.EqualTo("MDB02"), "An explicit database overrides its exclusion from automatic distribution.");
    }

    [Test, Order(5)]
    public async Task EnableMailbox_Shared_CreatesASharedMailboxOnAPlainAccountAsync()
    {
        await RunCleanAsync("Disable-Mailbox (b, again)", ps => ps.AddCommand("Disable-Mailbox").AddParameter("Identity", SharedB)
            .AddParameter("Confirm", false).AddParameter("DomainController", DomainController));
        await RunCleanAsync("Enable-Mailbox -Shared", ps => ps.AddCommand("Enable-Mailbox").AddParameter("Identity", SharedB)
            .AddParameter("Shared", true).AddParameter("DomainController", DomainController));

        Assert.That(await RecipientTypeDetailsAsync(SharedB), Is.EqualTo("SharedMailbox"));
    }

    [Test, Order(6)]
    public async Task EnableMailUser_GivesAPlainAccountAnExternalAddressAsync()
    {
        await RunCleanAsync("New-Mailbox -Shared (c)", ps => ps.AddCommand("New-Mailbox").AddParameter("Shared", true)
            .AddParameter("Name", MailUserC).AddParameter("Alias", MailUserC).AddParameter("DomainController", DomainController));
        _cleanup.Add(("Remove-MailUser", MailUserC));
        await RunCleanAsync("Disable-Mailbox (c)", ps => ps.AddCommand("Disable-Mailbox").AddParameter("Identity", MailUserC)
            .AddParameter("Confirm", false).AddParameter("DomainController", DomainController));
        await RunCleanAsync("Enable-MailUser", ps => ps.AddCommand("Enable-MailUser").AddParameter("Identity", MailUserC)
            .AddParameter("ExternalEmailAddress", $"{MailUserC}@example.org").AddParameter("DomainController", DomainController));

        Assert.That(await RecipientTypeDetailsAsync(MailUserC), Is.EqualTo("MailUser"));
    }

    [Test, Order(7)]
    public async Task EnableMailContact_MailEnablesAPlainContactAsync()
    {
        await RunCleanAsync("New-MailContact", ps => ps.AddCommand("New-MailContact").AddParameter("Name", ContactD).AddParameter("Alias", ContactD)
            .AddParameter("ExternalEmailAddress", $"{ContactD}@example.org").AddParameter("DomainController", DomainController));
        _cleanup.Add(("Remove-MailContact", ContactD));
        await RunCleanAsync("Disable-MailContact", ps => ps.AddCommand("Disable-MailContact").AddParameter("Identity", ContactD)
            .AddParameter("Confirm", false).AddParameter("DomainController", DomainController));
        await RunCleanAsync("Enable-MailContact", ps => ps.AddCommand("Enable-MailContact").AddParameter("Identity", ContactD)
            .AddParameter("ExternalEmailAddress", $"{ContactD}@example.org").AddParameter("DomainController", DomainController));

        Assert.That(await RecipientTypeDetailsAsync(ContactD), Is.EqualTo("MailContact"));
    }

    [Test, Order(8)]
    public async Task EnableDistributionGroup_MailEnablesAUniversalGroupAsync()
    {
        await RunCleanAsync("New-DistributionGroup", ps => ps.AddCommand("New-DistributionGroup").AddParameter("Name", GroupE).AddParameter("Alias", GroupE)
            .AddParameter("Type", "Distribution").AddParameter("DomainController", DomainController));
        _cleanup.Add(("Remove-DistributionGroup", GroupE));
        await RunCleanAsync("Disable-DistributionGroup", ps => ps.AddCommand("Disable-DistributionGroup").AddParameter("Identity", GroupE)
            .AddParameter("Confirm", false).AddParameter("DomainController", DomainController));
        await RunCleanAsync("Enable-DistributionGroup", ps => ps.AddCommand("Enable-DistributionGroup").AddParameter("Identity", GroupE)
            .AddParameter("DomainController", DomainController));

        Assert.That(await RecipientTypeDetailsAsync(GroupE), Is.EqualTo("MailUniversalDistributionGroup"));
    }

    [Test, Order(9)]
    public async Task Failures_AreClassifiedFromExchangesOwnRecordsAsync()
    {
        // Already enabled: A has a mailbox.
        var (_, alreadyEnabled, _) = await RunAsync("Enable-Mailbox (already)", ps => ps.AddCommand("Enable-Mailbox").AddParameter("Identity", SharedA)
            .AddParameter("DomainController", DomainController).AddParameter("ErrorAction", "Continue"));
        // Not found: what replication lag looks like when Exchange reads a domain controller without the object.
        var (_, notFound, _) = await RunAsync("Enable-Mailbox (missing)", ps => ps.AddCommand("Enable-Mailbox").AddParameter("Identity", $"{_prefix}-missing")
            .AddParameter("DomainController", DomainController).AddParameter("ErrorAction", "Continue"));
        // Outside svc-jim's roles: Exchange's session does not offer the cmdlet at all.
        var (_, outsideRoles, thrown) = await RunAsync("Get-ExchangeCertificate (outside roles)", ps => ps.AddCommand("Get-ExchangeCertificate"));

        foreach (var (name, record) in alreadyEnabled.Select(r => ("already enabled", r)).Concat(notFound.Select(r => ("not found", r))).Concat(outsideRoles.Select(r => ("outside roles", r))))
        {
            var failure = RemoteFailure.FromErrorRecord(record);
            TestContext.Out.WriteLine($"[{name}] Kind={failure.Kind} FailureCategory={failure.FailureCategory} Reason={failure.Reason} ErrorId={failure.ErrorId} Message={failure.Message}");
        }

        if (thrown != null)
        {
            var failure = RemoteFailure.FromException(thrown);
            TestContext.Out.WriteLine($"[outside roles, thrown] {thrown.GetType().Name}: Kind={failure.Kind} Message={failure.Message}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(alreadyEnabled, Has.Count.EqualTo(1), "Enabling a mailbox twice is refused.");
            Assert.That(notFound, Has.Count.EqualTo(1));
            Assert.That(notFound.FirstOrDefault()?.FullyQualifiedErrorId, Does.Contain("ManagementObjectNotFound"));
        }
    }

    [Test, Order(10)]
    public async Task GetRecipient_RepeatedOnOneSession_ReportsLatencyAsync()
    {
        var samples = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var (output, errors, thrown) = await RunAsync(null, ps => ps.AddCommand("Get-Recipient").AddParameter("Identity", SharedA)
                .AddParameter("DomainController", DomainController));
            samples.Add(stopwatch.ElapsedMilliseconds);
            Assert.That(thrown == null && errors.Count == 0 && output.Count == 1, $"Get-Recipient call {i + 1} failed.");
        }

        samples.Sort();
        TestContext.Out.WriteLine($"Get-Recipient x50 on one session: median {samples[24]} ms, p95 {samples[47]} ms, max {samples[49]} ms, mean {samples.Average():F0} ms.");
    }

    [Test, Order(11)]
    public async Task ConcurrentSessions_EightOpenAndAnswerTogetherAsync()
    {
        const int sessions = 8;
        var stopwatch = Stopwatch.StartNew();
        var runspaces = await Task.WhenAll(Enumerable.Range(0, sessions).Select(_ => RemoteSessionFactory.OpenAsync(ExchangeLab.Options())));
        var opened = stopwatch.ElapsedMilliseconds;
        try
        {
            stopwatch.Restart();
            var results = await Task.WhenAll(runspaces.Select(async runspace =>
            {
                using var ps = PowerShell.Create(runspace);
                ps.AddCommand("Get-Recipient").AddParameter("Identity", SharedA).AddParameter("DomainController", DomainController);
                return (await ps.InvokeAsync()).Count;
            }));
            TestContext.Out.WriteLine($"{sessions} concurrent sessions opened in {opened} ms and each ran Get-Recipient within {stopwatch.ElapsedMilliseconds} ms.");
            Assert.That(results, Has.All.EqualTo(1));
        }
        finally
        {
            foreach (var runspace in runspaces)
                runspace.Dispose();
        }
    }

    private async Task RunCleanAsync(string step, Action<PowerShell> build)
    {
        var (_, errors, thrown) = await RunAsync(step, build);
        if (thrown != null)
            Assert.Fail($"{step} threw {thrown.GetType().Name}: {thrown.Message}");
        Assert.That(errors, Is.Empty, $"{step} wrote errors: {string.Join(" | ", errors)}");
    }

    private async Task<string?> RecipientTypeDetailsAsync(string identity) =>
        (await GetSingleAsync("Get-Recipient", identity)).Properties["RecipientTypeDetails"]?.Value?.ToString();

    private async Task<PSObject> GetSingleAsync(string command, string identity)
    {
        var (output, errors, thrown) = await RunAsync(command, ps => ps.AddCommand(command).AddParameter("Identity", identity)
            .AddParameter("DomainController", DomainController));
        Assert.That(thrown == null && errors.Count == 0 && output.Count == 1, $"{command} {identity} did not return exactly one object: {thrown?.Message} {string.Join(" | ", errors)}");
        return output[0];
    }

    private async Task<(IReadOnlyList<PSObject> Output, IReadOnlyList<ErrorRecord> Errors, Exception? Thrown)> RunAsync(string? step, Action<PowerShell> build)
    {
        using var ps = PowerShell.Create(_runspace!);
        build(ps);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var output = await ps.InvokeAsync();
            return (output.ToList(), ps.Streams.Error.ToList(), null);
        }
        catch (RuntimeException error)
        {
            return ([], ps.Streams.Error.ToList(), error);
        }
        finally
        {
            if (step != null)
                _timings.Add((step, stopwatch.ElapsedMilliseconds));
        }
    }
}
