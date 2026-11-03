// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting.Kerberos;

namespace Tetron.PowerShellRemoting.Transport;

/// <summary>
/// A session's login to the KDC: one ticket-granting ticket, obtained when the first connection needs it and shared by
/// every connection after, and obtained again only when it expires.
/// </summary>
internal sealed class KerberosLogin : IDisposable
{
    private readonly RemoteSessionOptions _options;
    private readonly RemoteSessionStatistics _statistics;
    private readonly Lock _gate = new();
    private KerberosCredential? _current;
    private bool _disposed;

    public KerberosLogin(RemoteSessionOptions options, RemoteSessionStatistics statistics)
    {
        _options = options;
        _statistics = statistics;
    }

    /// <summary>The session's credential, logging in first if no connection has needed one yet.</summary>
    public KerberosCredential Current
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _current ??= LogIn();
            }
        }
    }

    /// <summary>
    /// Logs in again because <paramref name="expired"/> has expired, unless another connection already has, and returns
    /// the credential to use.
    /// </summary>
    public KerberosCredential Renew(KerberosCredential expired)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is not null && !ReferenceEquals(_current, expired))
                return _current;

            // Contexts still built on the expired credential hold their own reference to it, so it can go now.
            var replaced = _current;
            _current = LogIn();
            replaced?.Dispose();
            return _current;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            // A connection still part way through a handshake holds its own reference; the handle goes when it is done.
            _current?.Dispose();
            _current = null;
        }
    }

    private KerberosCredential LogIn()
    {
        var credential = KerberosCredential.Acquire(_options.UserPrincipalName, _options.Password);
        _statistics.RecordKerberosLogin();
        return credential;
    }
}
