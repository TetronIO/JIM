// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting.Kerberos;

namespace Tetron.PowerShellRemoting.Tests.Kerberos;

/// <summary>
/// The one Kerberos configuration a test run uses. <c>KRB5_CONFIG</c> is process-wide, so the harness and the lab
/// tests share a single krb5.conf holding every realm the environment configures, written the first time any of them
/// needs it.
/// </summary>
internal static class TestKerberosConfiguration
{
    private static readonly Lazy<string> ConfigurationPath = new(Write);

    internal static void Apply() => _ = ConfigurationPath.Value;

    private static string Write()
    {
        var realms = new List<KerberosRealm>();
        if (!string.IsNullOrWhiteSpace(KerberosHarness.Kdc))
            realms.Add(new KerberosRealm(KerberosHarness.Realm, [KerberosHarness.Kdc!]));
        if (ExchangeLab.IsConfigured)
            realms.Add(new KerberosRealm(ExchangeLab.Realm, [ExchangeLab.Kdc]));

        var path = Path.Combine(Path.GetTempPath(), $"tetron-remoting-tests-{Environment.ProcessId}.krb5.conf");
        Krb5Configuration.WriteAsync(path, realms).GetAwaiter().GetResult();
        KerberosEnvironment.UseConfiguration(path);
        return path;
    }
}
