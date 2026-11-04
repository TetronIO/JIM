// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Tetron.PowerShellRemoting.Tests.Kerberos;

/// <summary>
/// Settings for tests that need the remoting harness's Kerberos realm (test/integration/remoting-harness). They are
/// skipped unless <c>JIM_REMOTING_HARNESS_KDC</c> names the KDC and <c>JIM_REMOTING_HARNESS_KEYTAB</c> the service's
/// keytab, which the harness's setup script exports.
/// </summary>
internal static partial class KerberosHarness
{
    internal const string Realm = "HARNESS.TEST";
    internal const string ClientPrincipal = "svc-winrm@HARNESS.TEST";
    internal const string ClientPassword = "Client@123!";
    internal const string ServiceHost = "winrm-harness.harness.test";

    /// <summary>How long the stand-in server keeps an idle connection (docker-compose.yml, WINRM_IDLE_TIMEOUT_SECONDS).</summary>
    internal static readonly TimeSpan ServerIdleTimeout = TimeSpan.FromSeconds(5);

    private static readonly object Gate = new();
    private static bool _configured;

    internal static string? Kdc => Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_KDC");

    internal static string? Keytab => Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_KEYTAB");

    /// <summary>The stand-in WinRM server's address, from <c>JIM_REMOTING_HARNESS_WINRM</c>.</summary>
    internal static string? WinRM => Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_WINRM");

    /// <summary>The harness test CA that signed the stand-in server's TLS certificate, from <c>JIM_REMOTING_HARNESS_CA</c>.</summary>
    internal static string? CaCertificate => Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_CA");

    /// <summary>
    /// Whether a missing harness is a failure rather than a reason to skip, from <c>JIM_REMOTING_HARNESS_REQUIRED</c>.
    /// CI sets it, so a harness that failed to come up cannot pass the job by skipping every test.
    /// </summary>
    internal static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Skips the calling test unless the harness is configured; otherwise points Kerberos at it.</summary>
    internal static void RequireHarness()
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Kdc) || string.IsNullOrWhiteSpace(Keytab))
        {
            Unavailable("Needs the remoting harness: set JIM_REMOTING_HARNESS_KDC and JIM_REMOTING_HARNESS_KEYTAB.");
            return;
        }

        lock (Gate)
        {
            if (_configured)
                return;

            TestKerberosConfiguration.Apply();
            if (SetNativeEnvironmentVariable("KRB5_KTNAME", $"FILE:{Keytab}", 1) != 0)
                throw new InvalidOperationException("Could not set KRB5_KTNAME.");
            _configured = true;
        }
    }

    /// <summary>
    /// Options for a session to the stand-in WinRM server: <c>http</c> on 5985 (Kerberos-encrypted) or <c>https</c> on
    /// 5986. HTTPS is reached by the certificate's name, which the test host resolves through /etc/hosts; the SPN is
    /// given explicitly either way.
    /// </summary>
    internal static RemoteSessionOptions SessionOptions(string scheme = "http", int? port = null, bool trustHarnessCa = false,
        TimeSpan? operationTimeout = null, TimeSpan? requestTimeout = null, string? host = null)
    {
        var trusted = new X509Certificate2Collection();
        if (trustHarnessCa)
            trusted.Add(X509CertificateLoader.LoadCertificateFromFile(CaCertificate!));

        var defaults = new RemoteSessionOptions { ConnectionUri = new Uri("http://unused"), UserPrincipalName = ClientPrincipal, Password = ClientPassword };
        host ??= scheme == "https" ? ServiceHost : WinRM;
        port ??= scheme == "https" ? 5986 : 5985;
        return new RemoteSessionOptions
        {
            ConnectionUri = new Uri($"{scheme}://{host}:{port}/wsman"),
            UserPrincipalName = ClientPrincipal,
            Password = ClientPassword,
            ServicePrincipalHost = ServiceHost,
            TrustedCertificates = trusted,
            OperationTimeout = operationTimeout ?? defaults.OperationTimeout,
            RequestTimeout = requestTimeout ?? defaults.RequestTimeout
        };
    }

    /// <summary>Skips the calling test for want of part of the harness, or fails it when the harness is required.</summary>
    internal static void Unavailable(string reason)
    {
        if (IsRequired)
            Assert.Fail($"{reason} JIM_REMOTING_HARNESS_REQUIRED is set, so a missing harness fails rather than skips.");
        else
            Assert.Ignore(reason);
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetNativeEnvironmentVariable(string name, string value, int overwrite);
}
