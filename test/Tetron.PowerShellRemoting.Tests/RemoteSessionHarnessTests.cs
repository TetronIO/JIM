// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography.X509Certificates;
using Tetron.PowerShellRemoting.Tests.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// End to end through the harness: PowerShell's own remoting engine, over this library's WinRM transport, Kerberos
/// authentication and message encryption, to the stand-in WinRM server and its pwsh -s. Skipped without the harness.
/// </summary>
[Category("RequiresRemotingHarness")]
public class RemoteSessionHarnessTests
{
    [SetUp]
    public void SetUp()
    {
        KerberosHarness.RequireHarness();
        if (string.IsNullOrWhiteSpace(KerberosHarness.WinRM))
            KerberosHarness.Unavailable("Needs JIM_REMOTING_HARNESS_WINRM.");
    }

    [Test]
    public async Task OpenAsync_OverEncryptedHttp_RunsACommandWithBoundParametersAsync()
    {
        using var runspace = await RemoteSessionFactory.OpenAsync(Options("http", 5985));
        using var ps = PowerShell.Create(runspace);

        ps.AddCommand("Get-Date").AddParameter("Year", 2026).AddParameter("Month", 10).AddParameter("Day", 30);
        var output = await ps.InvokeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ps.HadErrors, Is.False);
            Assert.That(output, Has.Count.EqualTo(1));
            Assert.That(output[0].Properties["Year"]?.Value, Is.EqualTo(2026));
            Assert.That(runspace.RunspaceStateInfo.State, Is.EqualTo(RunspaceState.Opened));
        }
    }

    [Test]
    public async Task InvokeAsync_FailingCommand_ReturnsItsErrorRecordAsync()
    {
        using var runspace = await RemoteSessionFactory.OpenAsync(Options("http", 5985));
        using var ps = PowerShell.Create(runspace);

        ps.AddCommand("Get-Item").AddParameter("LiteralPath", "/no/such/path").AddParameter("ErrorAction", "Continue");
        await ps.InvokeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ps.HadErrors, Is.True);
            Assert.That(ps.Streams.Error, Has.Count.EqualTo(1));
            Assert.That(ps.Streams.Error[0].FullyQualifiedErrorId, Does.StartWith("PathNotFound"));
        }
    }

    [Test]
    public async Task InvokeAsync_SeveralCommandsOnOneRunspace_EachRunsInTurnAsync()
    {
        using var runspace = await RemoteSessionFactory.OpenAsync(Options("http", 5985));

        for (var i = 1; i <= 3; i++)
        {
            using var ps = PowerShell.Create(runspace);
            ps.AddCommand("Write-Output").AddParameter("InputObject", $"call {i}");
            var output = await ps.InvokeAsync();
            Assert.That(output.Single().BaseObject, Is.EqualTo($"call {i}"));
        }
    }

    [Test]
    public async Task OpenAsync_OverHttpsWithTheHarnessCa_RunsACommandAsync()
    {
        using var runspace = await RemoteSessionFactory.OpenAsync(Options("https", 5986, trustHarnessCa: true));
        using var ps = PowerShell.Create(runspace);

        ps.AddCommand("Write-Output").AddParameter("InputObject", "over TLS");
        var output = await ps.InvokeAsync();

        Assert.That(output.Single().BaseObject, Is.EqualTo("over TLS"));
    }

    [Test]
    public void OpenAsync_OverHttpsWithoutTrustingTheCa_IsRefused()
    {
        Assert.That(async () => await RemoteSessionFactory.OpenAsync(Options("https", 5986, trustHarnessCa: false)),
            Throws.InstanceOf<Exception>().With.Message.Contains("SSL").IgnoreCase.Or.Message.Contains("certificate").IgnoreCase);
    }

    [Test]
    public void OpenAsync_WrongPassword_FailsWithTheKerberosReason()
    {
        var options = Options("http", 5985);
        var wrong = new RemoteSessionOptions
        {
            ConnectionUri = options.ConnectionUri,
            UserPrincipalName = options.UserPrincipalName,
            Password = "not-the-password",
            ServicePrincipalHost = options.ServicePrincipalHost
        };

        Assert.That(async () => await RemoteSessionFactory.OpenAsync(wrong),
            Throws.InstanceOf<Exception>().With.Message.Contains("Kerberos").And.Message.Not.Contains("not-the-password"));
    }

    private static RemoteSessionOptions Options(string scheme, int port, bool trustHarnessCa = false)
    {
        var trusted = new X509Certificate2Collection();
        if (trustHarnessCa)
            trusted.Add(X509CertificateLoader.LoadCertificateFromFile(KerberosHarness.CaCertificate!));

        // The endpoint is reached by address, so the certificate's name is supplied by resolving it in /etc/hosts
        // on the test host; the SPN is given explicitly either way.
        var host = scheme == "https" ? KerberosHarness.ServiceHost : KerberosHarness.WinRM;
        return new RemoteSessionOptions
        {
            ConnectionUri = new Uri($"{scheme}://{host}:{port}/wsman"),
            UserPrincipalName = KerberosHarness.ClientPrincipal,
            Password = KerberosHarness.ClientPassword,
            ServicePrincipalHost = KerberosHarness.ServiceHost,
            TrustedCertificates = trusted
        };
    }
}
