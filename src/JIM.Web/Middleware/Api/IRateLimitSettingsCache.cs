// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Middleware.Api;

/// <summary>
/// A short-TTL cache over the API rate limiting Service Settings, so the rate limiter (which runs on every
/// REST API request) does not hit the database per request. See <see cref="RateLimitSettingsCache"/>.
/// </summary>
public interface IRateLimitSettingsCache
{
    /// <summary>
    /// Returns the current settings snapshot, refreshing from the database if the cached value has expired.
    /// </summary>
    Task<RateLimitSettingsSnapshot> GetSnapshotAsync();
}
