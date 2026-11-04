// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Management.Automation;
using Tetron.PowerShellRemoting.Tests.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// The spike's Phase 2 (#2021), Q6 through the harness: one runspace serving a batch of commands, as an export batch
/// would, without logging in to the KDC or opening a connection per command. Skipped without the harness.
/// </summary>
[Category("RequiresRemotingHarness")]
public class RemoteSessionReuseHarnessTests
{
    private const int Commands = 200;

    [SetUp]
    public void SetUp()
    {
        KerberosHarness.RequireHarness();
        if (string.IsNullOrWhiteSpace(KerberosHarness.WinRM))
            KerberosHarness.Unavailable("Needs JIM_REMOTING_HARNESS_WINRM.");
    }

    [Test]
    public async Task InvokeAsync_ManyCommandsOnOneRunspace_LogInOnceAndReuseConnectionsAsync()
    {
        var opening = Stopwatch.StartNew();
        using var runspace = await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions());
        opening.Stop();

        var latencies = new List<double>(Commands);
        for (var i = 1; i <= Commands; i++)
        {
            using var ps = PowerShell.Create(runspace);
            ps.AddCommand("Write-Output").AddParameter("InputObject", $"call {i}");
            var stopwatch = Stopwatch.StartNew();
            var output = await ps.InvokeAsync();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.That(output.Single().BaseObject, Is.EqualTo($"call {i}"));
        }

        var statistics = RemoteSessionFactory.GetStatistics(runspace);
        latencies.Sort();
        TestContext.Out.WriteLine($"Opened in {opening.ElapsedMilliseconds} ms. {Commands} commands: median {latencies[Commands / 2]:F0} ms, " +
            $"95th percentile {latencies[(int)(Commands * 0.95)]:F0} ms. Kerberos logins {statistics.KerberosLogins}, " +
            $"connections {statistics.ConnectionsOpened}, commands {statistics.CommandsRun}.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statistics.CommandsRun, Is.EqualTo(Commands));
            Assert.That(statistics.KerberosLogins, Is.EqualTo(1), "One ticket-granting ticket serves the whole session.");
            Assert.That(statistics.ConnectionsOpened, Is.LessThanOrEqualTo(4), "Finished commands hand their connection to the next.");
        }
    }

    [Test]
    public async Task InvokeAsync_AfterTheServerClosesIdleConnections_AuthenticatesAgainAndCarriesOnAsync()
    {
        using var runspace = await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions());
        Assert.That(await EchoAsync(runspace, "before"), Is.EqualTo("before"));
        var before = RemoteSessionFactory.GetStatistics(runspace).ConnectionsOpened;

        // Every connection the session holds is now idle long enough for the server to close it.
        await Task.Delay(KerberosHarness.ServerIdleTimeout + TimeSpan.FromSeconds(2));

        Assert.That(await EchoAsync(runspace, "after"), Is.EqualTo("after"));
        var statistics = RemoteSessionFactory.GetStatistics(runspace);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(statistics.ConnectionsOpened, Is.GreaterThan(before), "The closed connections were authenticated again.");
            Assert.That(statistics.KerberosLogins, Is.EqualTo(1), "Authenticating a connection again needs no new login.");
        }
    }

    private static async Task<object> EchoAsync(System.Management.Automation.Runspaces.Runspace runspace, string text)
    {
        using var ps = PowerShell.Create(runspace);
        ps.AddCommand("Write-Output").AddParameter("InputObject", text);
        return (await ps.InvokeAsync()).Single().BaseObject;
    }
}
