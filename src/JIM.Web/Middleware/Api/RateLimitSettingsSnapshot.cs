// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// An immutable, point-in-time read of the API rate limiting Service Settings, as served by
/// <see cref="IRateLimitSettingsCache"/>.
/// </summary>
/// <param name="Enabled">Whether API rate limiting is enabled at all.</param>
/// <param name="AuthenticatedRequestsPerMinute">The per-principal limit for authenticated requests.</param>
/// <param name="UnauthenticatedRequestsPerMinute">The per-IP limit for unauthenticated requests.</param>
/// <param name="RetrievedUtc">When this snapshot was read (or deemed unreadable and defaulted), used by the cache to decide when to refresh.</param>
public sealed record RateLimitSettingsSnapshot(
    bool Enabled,
    int AuthenticatedRequestsPerMinute,
    int UnauthenticatedRequestsPerMinute,
    DateTime RetrievedUtc)
{
    /// <summary>
    /// The compiled-in defaults, used before the database has ever been read (for example the very first
    /// request after startup, or a database outage on the first attempt). <see cref="RetrievedUtc"/> is
    /// <see cref="DateTime.MinValue"/> so this value is always treated as stale, forcing a real read at the
    /// first opportunity rather than being cached for a full TTL window.
    /// </summary>
    public static readonly RateLimitSettingsSnapshot Defaults = new(
        Constants.RateLimitDefaults.Enabled,
        Constants.RateLimitDefaults.AuthenticatedRequestsPerMinute,
        Constants.RateLimitDefaults.UnauthenticatedRequestsPerMinute,
        DateTime.MinValue);
}
