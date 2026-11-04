// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Remoting;
using System.Management.Automation.Remoting.Client;
using System.Management.Automation.Runspaces;

namespace Tetron.PowerShellRemoting.Transport;

/// <summary>
/// Connection details PowerShell's remoting engine takes to build a remote runspace over this library's WinRM transport,
/// through PowerShell's public custom transport API.
/// </summary>
internal sealed class WinRMConnectionInfo : RunspaceConnectionInfo
{
    private readonly RemoteSessionOptions _options;

    public WinRMConnectionInfo(RemoteSessionOptions options)
        : this(options, new RemoteSessionStatistics())
    {
    }

    private WinRMConnectionInfo(RemoteSessionOptions options, RemoteSessionStatistics statistics)
    {
        _options = options;
        Statistics = statistics;
        OpenTimeout = (int)Math.Min(int.MaxValue, options.RequestTimeout.TotalMilliseconds * 2);
    }

    /// <summary>The session's running costs; shared with every clone PowerShell makes of these details.</summary>
    public RemoteSessionStatistics Statistics { get; }

    public override string ComputerName
    {
        get => _options.ConnectionUri.Host;
        set => throw new NotSupportedException("The computer is fixed by the connection URI.");
    }

    // The credential lives in RemoteSessionOptions and goes only to Kerberos; PowerShell never sees it.
    public override PSCredential Credential
    {
        get => null!;
        set => throw new NotSupportedException("Credentials are supplied in RemoteSessionOptions.");
    }

    public override AuthenticationMechanism AuthenticationMechanism
    {
        get => AuthenticationMechanism.Kerberos;
        set
        {
            if (value != AuthenticationMechanism.Kerberos)
                throw new NotSupportedException("Only Kerberos authentication is supported.");
        }
    }

    public override string CertificateThumbprint
    {
        get => null!;
        set => throw new NotSupportedException("Certificate authentication is not supported.");
    }

    public override RunspaceConnectionInfo Clone() => new WinRMConnectionInfo(_options, Statistics);

    public override BaseClientSessionTransportManager CreateClientSessionTransportManager(Guid instanceId, string sessionName, PSRemotingCryptoHelper cryptoHelper) =>
        new WinRMClientTransportManager(_options, Statistics, instanceId, cryptoHelper);
}
