// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Tetron.PowerShellRemoting.Tests.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// The spike's Phase 1b (#2021): this library against a real Exchange server, from Linux, over Exchange's documented
/// endpoint. Every command only reads. Skipped outside the lab; see <see cref="ExchangeLab"/>.
/// </summary>
/// <remarks>
/// There is deliberately no wrong-password test here: failed logons count towards svc-jim's lockout in the lab's
/// domain, and the harness already proves that failure path.
/// </remarks>
[Category("RequiresExchangeLab")]
public class ExchangeLabTests
{
    private Runspace? _runspace;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        ExchangeLab.RequireLab();

        var stopwatch = Stopwatch.StartNew();
        _runspace = await RemoteSessionFactory.OpenAsync(ExchangeLab.Options());
        TestContext.Out.WriteLine($"Opened {ExchangeLab.Options().ConnectionUri} as {ExchangeLab.Account} in {stopwatch.ElapsedMilliseconds} ms.");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _runspace?.Dispose();

    [Test]
    public void OpenAsync_ExchangeEndpoint_LeavesTheRunspaceOpen()
    {
        Assert.That(_runspace!.RunspaceStateInfo.State, Is.EqualTo(RunspaceState.Opened));
    }

    [Test]
    public async Task InvokeAsync_GetExchangeServer_ReturnsTheLabServerAsync()
    {
        var (output, errors) = await InvokeAsync(ps => ps.AddCommand("Get-ExchangeServer"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(output.Select(o => o.Properties["Name"]?.Value?.ToString()), Has.Some.EqualTo(ExchangeLab.ExchangeHost.Split('.')[0]).IgnoreCase);
        }
    }

    [Test]
    public async Task InvokeAsync_GetMailboxDatabase_ReturnsTheDatabaseExcludedFromDistributionAsync()
    {
        var (output, errors) = await InvokeAsync(ps => ps.AddCommand("Get-MailboxDatabase"));

        var excluded = output.SingleOrDefault(o => string.Equals(o.Properties["Name"]?.Value?.ToString(), "MDB02", StringComparison.OrdinalIgnoreCase));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(output, Has.Count.GreaterThanOrEqualTo(2));
            Assert.That(excluded, Is.Not.Null, "The lab's Exchange build creates MDB02.");
            Assert.That(excluded?.Properties["IsExcludedFromProvisioning"]?.Value, Is.EqualTo(true));
        }
    }

    [Test]
    public async Task InvokeAsync_GetUser_ReturnsTheServiceAccountAsync()
    {
        var (output, errors) = await InvokeAsync(ps => ps.AddCommand("Get-User").AddParameter("Identity", ExchangeLab.Account));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(output, Has.Count.EqualTo(1));
            Assert.That(output.FirstOrDefault()?.Properties["SamAccountName"]?.Value?.ToString(), Is.EqualTo(ExchangeLab.Account).IgnoreCase);
        }
    }

    [Test]
    public async Task InvokeAsync_MissingMailbox_ReturnsExchangesErrorRecordAsync()
    {
        var (_, errors) = await InvokeAsync(ps => ps.AddCommand("Get-Mailbox")
            .AddParameter("Identity", $"jim-spike-missing-{Guid.NewGuid():N}")
            .AddParameter("ErrorAction", "Continue"));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That($"{errors[0].CategoryInfo.Reason} {errors[0].FullyQualifiedErrorId}", Does.Contain("ManagementObjectNotFound"));
    }

    [Test]
    public async Task InvokeAsync_ManyResults_ArriveWholeAsync()
    {
        // Every cmdlet svc-jim's roles allow: enough output to span many WS-Management Receive responses.
        var (output, errors) = await InvokeAsync(ps => ps.AddCommand("Get-Command"));
        TestContext.Out.WriteLine($"Get-Command returned {output.Count} commands.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(output, Has.Count.GreaterThan(100));
            Assert.That(output.Select(o => o.Properties["Name"]?.Value?.ToString()), Has.Some.EqualTo("Enable-Mailbox"));

            // WinRM splits encrypted messages over 16 KB into parts for some protocols. A response this size decoding at
            // all is the proof that Exchange does not split a Kerberos-sealed one, or that the decoder reads it if it does.
            Assert.That(RemoteSessionFactory.GetStatistics(_runspace!).LargestResponseBytes, Is.GreaterThan(16 * 1024),
                "the responses really crossed 16 KB");
        }
    }

    /// <summary>
    /// A parameter of a megabyte, which PowerShell remoting has to fragment across several WS-Management messages, each
    /// within Exchange's maximum envelope size. Whatever Exchange makes of the identity, an answer from Exchange proves the
    /// whole of it arrived; a transport failure, or a broken runspace, is what this guards against.
    /// </summary>
    [Test]
    public async Task InvokeAsync_OneMegabyteParameter_ReachesExchangeWholeAsync()
    {
        var identity = $"jim-phase1-size-{LargePayloadHarnessTests.Payload(1024 * 1024)}";
        using var ps = PowerShell.Create(_runspace!);
        ps.AddCommand("Get-Recipient").AddParameter("Identity", identity).AddParameter("ErrorAction", "Continue");

        string answer;
        try
        {
            await ps.InvokeAsync();
            answer = string.Join(" | ", ps.Streams.Error.Select(e => $"{e.FullyQualifiedErrorId}: {e.Exception.Message[..Math.Min(200, e.Exception.Message.Length)]}"));
        }
        catch (RuntimeException error)
        {
            // A terminating error raised by Exchange (a parameter it refuses) is as much an answer as an error record.
            answer = $"{error.ErrorRecord?.FullyQualifiedErrorId}: {error.Message[..Math.Min(200, error.Message.Length)]}";
        }

        var statistics = RemoteSessionFactory.GetStatistics(_runspace!);
        TestContext.Out.WriteLine($"Exchange answered: {answer}");
        TestContext.Out.WriteLine($"Largest request {statistics.LargestRequestBytes} bytes, largest response {statistics.LargestResponseBytes} bytes.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer, Is.Not.Empty, "a missing recipient is an error from Exchange, not silence");
            Assert.That(statistics.LargestRequestBytes, Is.GreaterThan(16 * 1024));
            Assert.That(statistics.LargestRequestBytes, Is.LessThanOrEqualTo(500 * 1024),
                "ex1 refuses a request over its MaxEnvelopeSizekb (500) with HTTP 413, as it did the 1 MB parameter sent whole");
            Assert.That(_runspace!.RunspaceStateInfo.State, Is.EqualTo(RunspaceState.Opened));
        }
    }

    private async Task<(IReadOnlyList<PSObject> Output, IReadOnlyList<ErrorRecord> Errors)> InvokeAsync(Action<PowerShell> build)
    {
        using var ps = PowerShell.Create(_runspace!);
        build(ps);
        var output = await ps.InvokeAsync();
        return (output.ToList(), ps.Streams.Error.ToList());
    }
}
