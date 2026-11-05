// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using JIM.Models.Exchange;
using Tetron.PowerShellRemoting;

namespace JIM.Connectors.LDAP;

/// <summary>
/// An Exchange remote PowerShell session for the delivery lane (#2021): the reads and commands convergence needs, as
/// commands and bound parameters, which is all Exchange's restricted endpoint accepts. One runspace serves every change
/// the lane converges.
/// </summary>
internal sealed class LdapExchangeRemoteRecipientSession : ILdapExchangeRecipientSession
{
    private readonly Runspace _runspace;

    private LdapExchangeRemoteRecipientSession(Runspace runspace)
    {
        _runspace = runspace;
    }

    /// <summary>Opens the session. Throws as <see cref="RemoteSessionFactory.OpenAsync"/> does when the sign-in fails.</summary>
    internal static async Task<ILdapExchangeRecipientSession> OpenAsync(RemoteSessionOptions options, CancellationToken cancellationToken) =>
        new LdapExchangeRemoteRecipientSession(await RemoteSessionFactory.OpenAsync(options, cancellationToken));

    public ExchangeSessionStatistics Statistics
    {
        get
        {
            var statistics = RemoteSessionFactory.GetStatistics(_runspace);
            return new ExchangeSessionStatistics
            {
                KerberosLogins = Clamp(statistics.KerberosLogins),
                Connections = Clamp(statistics.ConnectionsOpened),
                Commands = Clamp(statistics.CommandsRun)
            };
        }
    }

    public async Task<LdapExchangeRecipient> ReadAsync(ExchangeRecipientTarget target, CancellationToken cancellationToken)
    {
        // Get-User, Get-Contact and Get-Group find the object whether or not it is a recipient yet; Get-Recipient only
        // once it is one, so it is asked second, for the values JIM converges.
        var readCommand = LdapExchangeConvergePlanner.ReadCommandFor(target.ObjectType);
        var found = await InvokeAsync(new LdapExchangeCommand(readCommand, LdapExchangeConvergePlanner.Identify(target)), cancellationToken);
        var recipientType = LdapExchangeConvergePlanner.RecipientTypeOf(Text(found.FirstOrDefault(), "RecipientTypeDetails"));
        if (recipientType is ExchangeRecipientTypes.None or ExchangeRecipientTypes.Unmanaged)
            return new LdapExchangeRecipient(recipientType, null, null, null);

        var recipient = (await InvokeAsync(new LdapExchangeCommand("Get-Recipient", LdapExchangeConvergePlanner.Identify(target)), cancellationToken)).FirstOrDefault();
        return new LdapExchangeRecipient(recipientType, Text(recipient, "Alias"), Text(recipient, "PrimarySmtpAddress"), Text(recipient, "ExternalEmailAddress"));
    }

    public async Task RunAsync(LdapExchangeCommand command, CancellationToken cancellationToken) =>
        await InvokeAsync(command, cancellationToken);

    public void Dispose() => _runspace.Dispose();

    private async Task<List<PSObject>> InvokeAsync(LdapExchangeCommand command, CancellationToken cancellationToken)
    {
        using var ps = PowerShell.Create(_runspace);
        ps.AddCommand(command.Name);
        foreach (var (name, value) in command.Parameters)
        {
            if (value == null)
                ps.AddParameter(name);
            else
                ps.AddParameter(name, value);
        }

        await using var registration = cancellationToken.Register(() => ps.BeginStop(null, null));
        PSDataCollection<PSObject> output;
        try
        {
            output = await ps.InvokeAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new LdapExchangeCommandException(command.Name, RemoteFailure.FromException(ex, ps.InvocationStateInfo.State), ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (output)
        {
            // A non-terminating error (Exchange writes most of its refusals this way) fails the command as surely as a
            // terminating one.
            if (ps.Streams.Error.Count > 0)
                throw new LdapExchangeCommandException(command.Name, RemoteFailure.FromErrorRecord(ps.Streams.Error[0]));
            return [.. output];
        }
    }

    private static string? Text(PSObject? value, string property)
    {
        var text = value?.Properties[property]?.Value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int Clamp(long count) => count > int.MaxValue ? int.MaxValue : (int)count;
}
