// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using Tetron.PowerShellRemoting.Tests.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// The spike's Phase 2 (#2021): <see cref="RemoteFailure"/> classifies each failure as PowerShell actually reports it
/// through this transport, which the unit tests can only imitate. Skipped without the harness.
/// </summary>
[Category("RequiresRemotingHarness")]
public class RemoteFailureHarnessTests
{
    [SetUp]
    public void SetUp()
    {
        KerberosHarness.RequireHarness();
        if (string.IsNullOrWhiteSpace(KerberosHarness.WinRM))
            KerberosHarness.Unavailable("Needs JIM_REMOTING_HARNESS_WINRM.");
    }

    [Test]
    public async Task OpenAsync_WrongPassword_IsAnAuthenticationFailureAsync()
    {
        var options = KerberosHarness.SessionOptions();
        var failure = await OpenFailureAsync(new RemoteSessionOptions
        {
            ConnectionUri = options.ConnectionUri,
            UserPrincipalName = options.UserPrincipalName,
            Password = "not-the-password",
            ServicePrincipalHost = options.ServicePrincipalHost
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Authentication));
            Assert.That(failure.IsTransient, Is.False);
            Assert.That(failure.Message, Does.Not.Contain("not-the-password"));
        }
    }

    [Test]
    public async Task OpenAsync_UntrustedCertificate_IsACertificateFailureAsync()
    {
        var failure = await OpenFailureAsync(KerberosHarness.SessionOptions("https", trustHarnessCa: false));

        Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Certificate));
    }

    [Test]
    public async Task OpenAsync_ClosedPort_IsATransientConnectionFailureAsync()
    {
        var failure = await OpenFailureAsync(KerberosHarness.SessionOptions(port: 1, operationTimeout: TimeSpan.FromSeconds(5), requestTimeout: TimeSpan.FromSeconds(10)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Connection));
            Assert.That(failure.IsTransient, Is.True);
        }
    }

    [Test]
    public async Task OpenAsync_EndpointThatNeverAnswers_IsATransientFailureAsync()
    {
        // 192.0.2.1 is in TEST-NET-1 (RFC 5737). Depending on the network it times out or is unreachable; a retry may
        // succeed either way.
        var failure = await OpenFailureAsync(KerberosHarness.SessionOptions(host: "192.0.2.1", operationTimeout: TimeSpan.FromSeconds(3), requestTimeout: TimeSpan.FromSeconds(6)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Timeout).Or.EqualTo(RemoteFailureKind.Connection));
            Assert.That(failure.IsTransient, Is.True);
        }
    }

    [Test]
    public async Task InvokeAsync_CommandWritesAnError_IsACommandFailureNamingItsTargetAsync()
    {
        using var runspace = await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions());
        using var ps = PowerShell.Create(runspace);
        ps.AddCommand("Get-Item").AddParameter("LiteralPath", "/no/such/path").AddParameter("ErrorAction", "Continue");
        await ps.InvokeAsync();

        var failure = RemoteFailure.FromErrorRecord(ps.Streams.Error.Single());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Command));
            Assert.That(failure.ErrorId, Does.StartWith("PathNotFound"));
            Assert.That(failure.Reason, Is.EqualTo("ItemNotFoundException"));
            Assert.That(failure.TargetName, Is.EqualTo("/no/such/path"));
        }
    }

    [Test]
    public async Task InvokeAsync_CommandFailsOutright_IsACommandFailureAsync()
    {
        using var runspace = await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions());
        using var ps = PowerShell.Create(runspace);
        ps.AddCommand("Get-Item").AddParameter("LiteralPath", "/no/such/path").AddParameter("ErrorAction", "Stop");

        Exception? error = null;
        try
        {
            await ps.InvokeAsync();
        }
        catch (RuntimeException thrown)
        {
            error = thrown;
        }

        Assert.That(error, Is.Not.Null, "-ErrorAction Stop ends the invocation with an exception.");
        var failure = RemoteFailure.FromException(error!, ps.InvocationStateInfo.State);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Kind, Is.EqualTo(RemoteFailureKind.Command));
            Assert.That(failure.Message, Does.Contain("/no/such/path"));
            Assert.That(failure.ErrorId, Does.StartWith("PathNotFound"));
        }
    }

    private static async Task<RemoteFailure> OpenFailureAsync(RemoteSessionOptions options)
    {
        try
        {
            using var runspace = await RemoteSessionFactory.OpenAsync(options);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return RemoteFailure.FromException(error);
        }

        Assert.Fail("The session opened, but this test expects it to fail.");
        throw new InvalidOperationException("Unreachable.");
    }
}
