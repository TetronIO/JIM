// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Middleware.Api;

/// <summary>
/// The kind of rate limiting partition <see cref="RateLimitPartitionResolver"/> selected for a request.
/// </summary>
public enum RateLimitPartitionKind
{
    /// <summary>
    /// No limiter applies: the request is outside the REST API, is a health check, or rate limiting is disabled.
    /// </summary>
    NoLimiter,

    /// <summary>
    /// An authenticated request, partitioned per principal, throttled with a sliding window.
    /// </summary>
    AuthenticatedSlidingWindow,

    /// <summary>
    /// An unauthenticated request (including one that failed API key authentication), partitioned per client IP,
    /// throttled with a fixed window.
    /// </summary>
    UnauthenticatedFixedWindow
}
