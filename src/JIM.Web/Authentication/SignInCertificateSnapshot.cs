// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Application.Interfaces;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// The JIM certificate store's certificates trusted for sign-in, held in memory for the identity provider's TLS
/// validation callback (#2363).
/// </summary>
/// <remarks>
/// <para>
/// A validation callback is synchronous and cannot query the database, so the certificates are loaded ahead of each
/// request to the identity provider (<see cref="RefreshAsync"/>, called by the back-channel's handler) and read from
/// memory during the handshake. Requests to the identity provider are rare (configuration and signing keys, and one
/// token redemption per sign-in), so following the store this closely costs one small query per request at most,
/// and a <see cref="FreshFor"/> window bounds bursts. Following it this closely is what makes a certificate trusted
/// in the portal or through the API take effect on the next sign-in, without a restart.
/// </para>
/// <para>
/// When the store cannot be read (the database is still starting, or briefly unavailable) the previous certificates
/// stand: an empty snapshot would refuse an identity provider that was trusted a moment ago. Before the first
/// successful load it is empty, which leaves the operating system's own trust in force.
/// </para>
/// </remarks>
public sealed class SignInCertificateSnapshot
{
    /// <summary>
    /// How long a load is reused before the store is read again.
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<IReadOnlyCollection<X509Certificate2>>> _load;
    private readonly TimeProvider _time;
    private readonly TimeSpan _freshFor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile State _state = new([], 0, null, DateTimeOffset.MinValue);

    /// <summary>
    /// Reads the sign-in certificates through the application layer.
    /// </summary>
    public SignInCertificateSnapshot(IJimApplicationFactory applicationFactory)
        : this(async _ =>
        {
            using var jim = applicationFactory.Create();
            return await jim.Certificates.GetSignInTrustedCertificatesAsync();
        }, TimeProvider.System)
    {
    }

    /// <param name="load">Reads the certificates trusted for sign-in.</param>
    /// <param name="time">The clock the freshness window is measured on.</param>
    /// <param name="freshFor">Overrides <see cref="FreshFor"/>.</param>
    public SignInCertificateSnapshot(Func<CancellationToken, Task<IReadOnlyCollection<X509Certificate2>>> load, TimeProvider time, TimeSpan? freshFor = null)
    {
        _load = load;
        _time = time;
        _freshFor = freshFor ?? FreshFor;
    }

    /// <summary>
    /// The certificates as last loaded.
    /// </summary>
    public IReadOnlyCollection<X509Certificate2> Certificates => _state.Certificates;

    /// <summary>
    /// Changes whenever the set of certificates changes, so something that failed against an earlier set can tell
    /// that trying again might now succeed.
    /// </summary>
    public long Version => _state.Version;

    /// <summary>
    /// Reloads the certificates unless the last load is still fresh. Never throws for an unavailable store.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsFresh(_state))
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _state;
            if (IsFresh(current))
                return;

            var now = _time.GetUtcNow();
            try
            {
                var loaded = await _load(cancellationToken);
                var key = string.Join(',', loaded.Select(c => c.Thumbprint).Order(StringComparer.OrdinalIgnoreCase));
                var version = key == current.Key ? current.Version : current.Version + 1;
                _state = new State(loaded, version, key, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep what we had; the next request tries again once this attempt goes stale.
                Log.Warning(ex, "Could not read the certificates trusted for sign-in; keeping the {Count} already loaded", current.Certificates.Count);
                _state = current with { LoadedAt = now };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(State state) => _time.GetUtcNow() - state.LoadedAt < _freshFor;

    private sealed record State(IReadOnlyCollection<X509Certificate2> Certificates, long Version, string? Key, DateTimeOffset LoadedAt);
}
