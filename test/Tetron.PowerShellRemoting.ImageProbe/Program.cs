// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using Tetron.PowerShellRemoting;
using Tetron.PowerShellRemoting.Kerberos;

// Everything comes from the environment, so the same probe runs against the harness in CI or a lab server by hand.
// The password is never written out. Any failure is left to end the process, which prints it and exits non-zero.
static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is not set.");

// As JIM writes it: a krb5.conf of its own, in a private directory under the one writable path.
var configuration = Path.Combine(Directory.CreateTempSubdirectory("jim-kerberos-").FullName, "krb5.conf");
await Krb5Configuration.WriteAsync(configuration, [new KerberosRealm(Required("JIM_PROBE_REALM"), [Required("JIM_PROBE_KDC")])]);
KerberosEnvironment.UseConfiguration(configuration);

var options = new RemoteSessionOptions
{
    ConnectionUri = new Uri(Required("JIM_PROBE_URI")),
    ServicePrincipalHost = Environment.GetEnvironmentVariable("JIM_PROBE_SPN_HOST") is { Length: > 0 } spn ? spn : null,
    UserPrincipalName = Required("JIM_PROBE_UPN"),
    Password = Required("JIM_PROBE_PASSWORD")
};

using var runspace = await RemoteSessionFactory.OpenAsync(options);
using var ps = PowerShell.Create(runspace);
ps.AddCommand("Write-Output").AddParameter("InputObject", "probe");
var output = await ps.InvokeAsync();
if (ps.HadErrors || output.Count != 1 || output[0].BaseObject as string != "probe")
{
    Console.Error.WriteLine($"FAILED: the session opened, but the command returned {output.Count} result(s) and {ps.Streams.Error.Count} error(s).");
    return 1;
}

Console.WriteLine($"OK: opened {options.ConnectionUri} as {options.UserPrincipalName} and ran a command, running as {Environment.UserName}.");
return 0;
