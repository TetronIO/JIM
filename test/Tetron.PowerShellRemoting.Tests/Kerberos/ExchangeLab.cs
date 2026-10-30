// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.Tests.Kerberos;

/// <summary>
/// Settings for tests against the Active Directory lab's Exchange server (#2412), run from the lab's runner VM. They are
/// skipped unless <c>JIM_LAB_SVC_JIM_PASSWORD</c> holds svc-jim's password; the other values default to the lab's own.
/// </summary>
internal static class ExchangeLab
{
    internal static string? Password => Environment.GetEnvironmentVariable("JIM_LAB_SVC_JIM_PASSWORD");

    internal static string Realm => Setting("JIM_LAB_REALM", "PANOPLY.LOCAL");

    internal static string Kdc => Setting("JIM_LAB_KDC", "dc1.panoply.local");

    internal static string ExchangeHost => Setting("JIM_LAB_EXCHANGE_HOST", "ex1.panoply.local");

    /// <summary>The account JIM connects as: in Recipient Management, as the lab's Exchange build makes it.</summary>
    internal static string Account => Setting("JIM_LAB_ACCOUNT", "svc-jim");

    internal static bool IsConfigured => !OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Password);

    /// <summary>Skips the calling test unless the lab is configured; otherwise points Kerberos at it.</summary>
    internal static void RequireLab()
    {
        if (!IsConfigured)
        {
            Assert.Ignore("Needs the AD lab: set JIM_LAB_SVC_JIM_PASSWORD to svc-jim's password, on the lab's runner VM.");
            return;
        }

        TestKerberosConfiguration.Apply();
    }

    /// <summary>Exchange's own endpoint, as Microsoft documents it: HTTP, Kerberos, the payload Kerberos-encrypted.</summary>
    internal static RemoteSessionOptions Options() => new()
    {
        ConnectionUri = new Uri($"http://{ExchangeHost}/PowerShell/"),
        ConfigurationName = "Microsoft.Exchange",
        UserPrincipalName = $"{Account}@{Realm}",
        Password = Password!
    };

    private static string Setting(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
