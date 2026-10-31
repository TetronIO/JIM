// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Management.Automation;
using Tetron.PowerShellRemoting.Tests.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// The spike's Phase 2 (#2021) through the harness: stopping a command, a command that outlasts the server's
/// operation timeout, cancelling an open, and endpoints that refuse or never answer. Skipped without the harness.
/// </summary>
[Category("RequiresRemotingHarness")]
public class RemoteSessionHardeningHarnessTests
{
    [SetUp]
    public void SetUp()
    {
        KerberosHarness.RequireHarness();
        if (string.IsNullOrWhiteSpace(KerberosHarness.WinRM))
            KerberosHarness.Unavailable("Needs JIM_REMOTING_HARNESS_WINRM.");
    }

    [Test]
    public async Task StopAsync_LongRunningCommand_StopsItPromptlyAndTheRunspaceCarriesOnAsync()
    {
        using var runspace = await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions());

        using (var ps = PowerShell.Create(runspace))
        {
            ps.AddCommand("Start-Sleep").AddParameter("Seconds", 120);
            var invocation = ps.InvokeAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));

            var stopwatch = Stopwatch.StartNew();
            await ps.StopAsync(null, null);

            // A remote pipeline that is stopped ends the invocation with the server's own record of it, so it surfaces
            // as a RemoteException ("The pipeline has been stopped."), not a local PipelineStoppedException. The
            // invocation state is what says it was stopped rather than failed.
            Exception? ended = null;
            try
            {
                await invocation;
            }
            catch (Exception error)
            {
                ended = error;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ps.InvocationStateInfo.State, Is.EqualTo(PSInvocationState.Stopped));
                Assert.That(ended, Is.InstanceOf<PipelineStoppedException>().Or.InstanceOf<RemoteException>());
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
            }
        }

        using var next = PowerShell.Create(runspace);
        next.AddCommand("Write-Output").AddParameter("InputObject", "still here");
        Assert.That((await next.InvokeAsync()).Single().BaseObject, Is.EqualTo("still here"));
    }

    [Test]
    public async Task InvokeAsync_CommandOutlastsTheOperationTimeout_StillReturnsItsOutputAsync()
    {
        // The server answers each Receive after two seconds with no output yet; the transport must keep asking.
        var options = KerberosHarness.SessionOptions(operationTimeout: TimeSpan.FromSeconds(2), requestTimeout: TimeSpan.FromSeconds(10));
        using var runspace = await RemoteSessionFactory.OpenAsync(options);
        using var ps = PowerShell.Create(runspace);
        ps.AddCommand("Start-Sleep").AddParameter("Seconds", 7);
        ps.AddStatement().AddCommand("Write-Output").AddParameter("InputObject", "after the sleep");

        var stopwatch = Stopwatch.StartNew();
        var output = await ps.InvokeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ps.Streams.Error, Is.Empty);
            Assert.That(output.Single().BaseObject, Is.EqualTo("after the sleep"));
            Assert.That(stopwatch.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(7)));
        }
    }

    [Test]
    public void OpenAsync_CancelledWhileOpening_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        Assert.That(async () => await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions(), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void OpenAsync_ClosedPort_FailsFastSayingTheConnectionWasRefused()
    {
        var options = KerberosHarness.SessionOptions(port: 1, operationTimeout: TimeSpan.FromSeconds(5), requestTimeout: TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();
        Assert.That(async () => await RemoteSessionFactory.OpenAsync(options),
            Throws.InstanceOf<Exception>().With.Message.Contains("refused").IgnoreCase);
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void OpenAsync_EndpointThatNeverAnswers_GivesUpWithinTheRequestTimeoutSayingSo()
    {
        // 192.0.2.1 is in TEST-NET-1 (RFC 5737), where nothing answers.
        var options = KerberosHarness.SessionOptions(host: "192.0.2.1", operationTimeout: TimeSpan.FromSeconds(3), requestTimeout: TimeSpan.FromSeconds(6));

        var stopwatch = Stopwatch.StartNew();
        Assert.That(async () => await RemoteSessionFactory.OpenAsync(options),
            Throws.InstanceOf<Exception>().With.Message.Contains("192.0.2.1"));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
    }
}
