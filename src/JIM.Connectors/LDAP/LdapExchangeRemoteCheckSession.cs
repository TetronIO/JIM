// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Tetron.PowerShellRemoting;

namespace JIM.Connectors.LDAP;

/// <summary>
/// An Exchange remote PowerShell session for the LDAP Connector's settings check (#2021): the two read-only questions the
/// check asks, as commands and bound parameters, which is all Exchange's restricted endpoint accepts.
/// </summary>
internal sealed class LdapExchangeRemoteCheckSession : ILdapExchangeCheckSession
{
    private readonly Runspace _runspace;

    private LdapExchangeRemoteCheckSession(Runspace runspace)
    {
        _runspace = runspace;
    }

    /// <summary>
    /// Opens the session. Throws as <see cref="RemoteSessionFactory.OpenAsync"/> does when the sign-in fails.
    /// </summary>
    internal static async Task<ILdapExchangeCheckSession> OpenAsync(RemoteSessionOptions options, CancellationToken cancellationToken) =>
        new LdapExchangeRemoteCheckSession(await RemoteSessionFactory.OpenAsync(options, cancellationToken));

    public async Task<IReadOnlySet<string>> GetVisibleCommandsAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        // A name the account cannot see is a non-terminating error, silenced: what comes back is what it can see.
        using var ps = PowerShell.Create(_runspace);
        ps.AddCommand("Get-Command").AddParameter("Name", names.ToArray()).AddParameter("ErrorAction", "SilentlyContinue");

        using var output = await InvokeAsync(ps, cancellationToken);
        return output
            .Select(command => command.Properties["Name"]?.Value?.ToString())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string?> GetServerVersionAsync(string server, CancellationToken cancellationToken)
    {
        using var ps = PowerShell.Create(_runspace);
        ps.AddCommand("Get-ExchangeServer").AddParameter("Identity", server).AddParameter("ErrorAction", "SilentlyContinue");

        try
        {
            using var output = await InvokeAsync(ps, cancellationToken);
            return output.Select(found => found.Properties["AdminDisplayVersion"]?.Value?.ToString()).FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));
        }
        catch (RuntimeException)
        {
            // Hidden from an account without a configuration role, or a name Exchange does not know (a load balancer's):
            // the version is a nicety, not a reason to fail the check.
            return null;
        }
    }

    public void Dispose() => _runspace.Dispose();

    private static async Task<PSDataCollection<PSObject>> InvokeAsync(PowerShell ps, CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => ps.BeginStop(null, null));
        var output = await ps.InvokeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }
}
