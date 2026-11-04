// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;

namespace Tetron.PowerShellRemoting;

/// <summary>How to reach a Remote PowerShell endpoint and who to authenticate as.</summary>
public sealed class RemoteSessionOptions
{
    /// <summary>
    /// The WinRM endpoint. <c>http://</c> sessions are always Kerberos-encrypted and refuse anything unencrypted;
    /// <c>https://</c> sessions rely on TLS 1.2 or later. Exchange's endpoint is <c>http://server/PowerShell/</c>.
    /// </summary>
    public required Uri ConnectionUri { get; init; }

    /// <summary>The session configuration, for example <c>Microsoft.Exchange</c>. Defaults to <c>Microsoft.PowerShell</c>.</summary>
    public string ConfigurationName { get; init; } = "Microsoft.PowerShell";

    /// <summary>The account to authenticate as, as a Kerberos principal: <c>svc-jim@PANOPLY.LOCAL</c>.</summary>
    public required string UserPrincipalName { get; init; }

    /// <summary>
    /// The account's password. Used to obtain a Kerberos ticket and never sent to the server. It cannot be read back,
    /// so logging or serialising these options never carries it.
    /// </summary>
    public required string Password { internal get; init; }

    /// <summary>
    /// The host name in the service principal (<c>HTTP/host</c>). Defaults to the endpoint's host; set it when the
    /// endpoint is reached by a name or address other than the one the SPN is registered for.
    /// </summary>
    public string? ServicePrincipalHost { get; init; }

    /// <summary>CA certificates to trust, in addition to the system's, for an <c>https://</c> endpoint.</summary>
    public X509Certificate2Collection TrustedCertificates { get; init; } = [];

    /// <summary>How long the server may hold a Receive waiting for output before answering that there is none yet.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long one HTTP request may take, including the server's <see cref="OperationTimeout"/>.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The account, endpoint and session configuration, for logs; never the password.</summary>
    public override string ToString() => $"{UserPrincipalName} at {ConnectionUri} ({ConfigurationName})";

    internal string ServiceHost => ServicePrincipalHost ?? ConnectionUri.Host;

    internal bool UsesMessageEncryption => ConnectionUri.Scheme == Uri.UriSchemeHttp;

    internal RemoteSessionOptions Validate()
    {
        if (ConnectionUri.Scheme != Uri.UriSchemeHttp && ConnectionUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The connection URI must be http:// (Kerberos-encrypted) or https://.", nameof(ConnectionUri));
        ArgumentException.ThrowIfNullOrWhiteSpace(UserPrincipalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConfigurationName);
        if (!UserPrincipalName.Contains('@', StringComparison.Ordinal))
            throw new ArgumentException("The user principal name must include its realm, for example svc-jim@PANOPLY.LOCAL.", nameof(UserPrincipalName));
        if (OperationTimeout <= TimeSpan.Zero || RequestTimeout <= OperationTimeout)
            throw new ArgumentException("The request timeout must be longer than the operation timeout.", nameof(RequestTimeout));
        return this;
    }
}
