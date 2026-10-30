// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.InteropServices;
using Tetron.PowerShellRemoting.Kerberos;

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

    private static readonly object Gate = new();
    private static bool _configured;

    internal static string? Kdc => Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_KDC");

    internal static string? Keytab => Environment.GetEnvironmentVariable("JIM_REMOTING_HARNESS_KEYTAB");

    /// <summary>Skips the calling test unless the harness is configured; otherwise points Kerberos at it.</summary>
    internal static void RequireHarness()
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Kdc) || string.IsNullOrWhiteSpace(Keytab))
        {
            Assert.Ignore("Needs the remoting harness: set JIM_REMOTING_HARNESS_KDC and JIM_REMOTING_HARNESS_KEYTAB.");
            return;
        }

        lock (Gate)
        {
            if (_configured)
                return;

            var path = Path.Combine(Path.GetTempPath(), $"tetron-remoting-tests-{Environment.ProcessId}.krb5.conf");
            Krb5Configuration.WriteAsync(path, [new KerberosRealm(Realm, [Kdc!])]).GetAwaiter().GetResult();
            KerberosEnvironment.UseConfiguration(path);
            if (SetNativeEnvironmentVariable("KRB5_KTNAME", $"FILE:{Keytab}", 1) != 0)
                throw new InvalidOperationException("Could not set KRB5_KTNAME.");
            _configured = true;
        }
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetNativeEnvironmentVariable(string name, string value, int overwrite);
}
