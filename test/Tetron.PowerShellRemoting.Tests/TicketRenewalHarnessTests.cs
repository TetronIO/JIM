// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Tetron.PowerShellRemoting.Tests.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// A session outliving its ticket-granting ticket. Active Directory issues them for ten hours, and a Worker's session to
/// Exchange can be in use for longer, so the session has to sign in again when its ticket runs out rather than fail its
/// next command. The harness KDC is asked for two-and-a-half-minute tickets so this can be seen in minutes: it refuses
/// service tickets for a ticket-granting ticket with about two minutes or less to run, so nothing shorter can open a session.
/// Skipped without the harness.
/// </summary>
/// <remarks>
/// Non-parallelisable because the short tickets come from a krb5.conf the whole process reads.
/// </remarks>
[Category("RequiresRemotingHarness")]
[NonParallelizable]
public class TicketRenewalHarnessTests
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(150);

    // Past the ticket's end by a margin, so the expiry is certain on the client, which checks it against its own clock.
    private static readonly TimeSpan PastExpiry = TicketLifetime + TimeSpan.FromSeconds(15);

    [SetUp]
    public void SetUp()
    {
        KerberosHarness.RequireHarness();
        if (string.IsNullOrWhiteSpace(KerberosHarness.WinRM))
            KerberosHarness.Unavailable("Needs JIM_REMOTING_HARNESS_WINRM.");
    }

    [Test]
    public async Task InvokeAsync_AfterTheTicketGrantingTicketExpires_SignsInAgainAndCarriesOnAsync()
    {
        using var shortTickets = TestKerberosConfiguration.UseTicketLifetime(TicketLifetime);
        using var runspace = await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions());
        Assert.That(await EchoAsync(runspace, "before"), Is.EqualTo("before"));

        await Task.Delay(PastExpiry);
        var after = await EchoAsync(runspace, "after");

        var statistics = RemoteSessionFactory.GetStatistics(runspace);
        TestContext.Out.WriteLine($"Logins={statistics.KerberosLogins} Connections={statistics.ConnectionsOpened} Commands={statistics.CommandsRun}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after, Is.EqualTo("after"));
            Assert.That(runspace.RunspaceStateInfo.State, Is.EqualTo(RunspaceState.Opened));
            Assert.That(statistics.KerberosLogins, Is.EqualTo(2), "one login to open the session, and one once its ticket had run out");
        }
    }

    /// <summary>
    /// A command already running when the ticket runs out: its output arrives sealed with a context whose ticket has
    /// expired. A long export batch on a session opened hours earlier is the case this stands for.
    /// </summary>
    [Test]
    public async Task InvokeAsync_CommandRunningWhenTheTicketExpires_CompletesAsync()
    {
        using var shortTickets = TestKerberosConfiguration.UseTicketLifetime(TicketLifetime);
        using var runspace = await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions());

        using var ps = PowerShell.Create(runspace);
        ps.AddCommand("Start-Sleep").AddParameter("Seconds", (int)PastExpiry.TotalSeconds);
        await ps.InvokeAsync();
        var after = await EchoAsync(runspace, "after");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ps.HadErrors, Is.False);
            Assert.That(after, Is.EqualTo("after"));
            Assert.That(runspace.RunspaceStateInfo.State, Is.EqualTo(RunspaceState.Opened));
        }
    }

    private static async Task<object?> EchoAsync(Runspace runspace, string text)
    {
        using var ps = PowerShell.Create(runspace);
        ps.AddCommand("Write-Output").AddParameter("InputObject", text);
        var output = await ps.InvokeAsync();
        return output.Single().BaseObject;
    }
}
