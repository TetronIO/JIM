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

    /// <summary>
    /// Points Kerberos at a copy of the configuration that asks the KDC for tickets lasting <paramref name="lifetime"/>,
    /// until the result is disposed. For tests of what happens when a ticket runs out, which would otherwise wait the ten
    /// hours Active Directory issues them for. Kerberos rereads its configuration for every login, so only logins made
    /// while it is in force get the short tickets.
    /// </summary>
    internal static IDisposable UseTicketLifetime(TimeSpan lifetime)
    {
        var path = ConfigurationPath.Value;
        var text = File.ReadAllText(path);
        var shortened = text.Replace("[libdefaults]\n", $"[libdefaults]\n    ticket_lifetime = {(int)lifetime.TotalSeconds}s\n", StringComparison.Ordinal);
        if (shortened == text)
            throw new InvalidOperationException("The generated krb5.conf has no [libdefaults] section to shorten tickets in.");

        var shortPath = $"{path}.short-tickets";
        File.WriteAllText(shortPath, shortened);
        KerberosEnvironment.UseConfiguration(shortPath);
        return new Restore(path, shortPath);
    }

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

    private sealed class Restore(string path, string shortPath) : IDisposable
    {
        public void Dispose()
        {
            KerberosEnvironment.UseConfiguration(path);
            File.Delete(shortPath);
        }
    }
}
