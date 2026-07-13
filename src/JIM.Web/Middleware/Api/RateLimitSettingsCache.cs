// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Core;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// A thread-safe, short-TTL cache of the API rate limiting Service Settings. The global rate limiter's partition
/// factory runs on every REST API request and cannot be async-friendly about it (<c>PartitionedRateLimiter.Create</c>
/// takes a synchronous factory), so settings are read from the database in the background at most once per
/// <see cref="DefaultCacheDuration"/> rather than on the hot path. A Service Settings change is therefore visible to new
/// requests within this TTL, not instantly; that propagation delay is documented for administrators in the rate
/// limiting docs page.
/// </summary>
/// <remarks>
/// No existing generic settings cache covers this (JimApplication's optional <c>IMemoryCache</c> is wired up for
/// the Worker only; JIM.Web's <c>JimApplication</c> registration does not pass one, and
/// <c>ServiceSettingsServer.GetSettingValueAsync</c> always reads through to the repository). This is a small,
/// purpose-built cache rather than an attempt to retrofit generic caching onto every Service Setting read.
/// </remarks>
public sealed class RateLimitSettingsCache : IRateLimitSettingsCache
{
    /// <summary>The default cache TTL.</summary>
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The active cache TTL. Defaults to <see cref="DefaultCacheDuration"/>; settable so tests can verify
    /// expiry behaviour without a real 30-second wait. A single constructor (matching DI's expectations exactly)
    /// is kept deliberately, rather than an overload taking the TTL, to avoid any risk of the DI container
    /// selecting the wrong one.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = DefaultCacheDuration;

    private readonly IJimApplicationFactory _applicationFactory;
    private readonly ILogger<RateLimitSettingsCache> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private RateLimitSettingsSnapshot _snapshot = RateLimitSettingsSnapshot.Defaults;

    public RateLimitSettingsCache(IJimApplicationFactory applicationFactory, ILogger<RateLimitSettingsCache> logger)
    {
        _applicationFactory = applicationFactory;
        _logger = logger;
    }

    public async Task<RateLimitSettingsSnapshot> GetSnapshotAsync()
    {
        var current = Volatile.Read(ref _snapshot);
        if (DateTime.UtcNow - current.RetrievedUtc < CacheDuration)
            return current;

        // Only one caller refreshes at a time; everyone else keeps serving the (briefly) stale snapshot rather
        // than piling concurrent requests onto the database the instant the cache expires.
        if (!await _refreshGate.WaitAsync(0))
            return current;

        try
        {
            // Re-check: another request may have refreshed while this one waited for the gate.
            current = Volatile.Read(ref _snapshot);
            if (DateTime.UtcNow - current.RetrievedUtc < CacheDuration)
                return current;

            var refreshed = await LoadFromDatabaseAsync(current);
            Volatile.Write(ref _snapshot, refreshed);
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<RateLimitSettingsSnapshot> LoadFromDatabaseAsync(RateLimitSettingsSnapshot previous)
    {
        try
        {
            using var jim = _applicationFactory.Create();
            var enabled = await jim.ServiceSettings.GetSettingValueAsync(
                Constants.SettingKeys.RateLimitingEnabled, Constants.RateLimitDefaults.Enabled);
            var authenticatedLimit = await jim.ServiceSettings.GetSettingValueAsync(
                Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute, Constants.RateLimitDefaults.AuthenticatedRequestsPerMinute);
            var unauthenticatedLimit = await jim.ServiceSettings.GetSettingValueAsync(
                Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute, Constants.RateLimitDefaults.UnauthenticatedRequestsPerMinute);

            return new RateLimitSettingsSnapshot(enabled, authenticatedLimit, unauthenticatedLimit, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fallback-dispatcher catch: a settings read failure (e.g. transient database outage) must not take
            // down every REST API request, so fall back to the last known-good snapshot (or the compiled-in
            // defaults, before any successful read has ever happened). The timestamp is refreshed so a prolonged
            // outage retries at most once per cache window rather than on every request.
            _logger.LogWarning(ex, "RateLimitSettingsCache: failed to refresh rate limiting settings from the database; continuing with the last known values.");
            return previous with { RetrievedUtc = DateTime.UtcNow };
        }
    }
}
