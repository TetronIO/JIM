// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Middleware.Api;

/// <summary>
/// The outcome of <see cref="RateLimitPartitionResolver.Resolve"/>: which kind of limiter (if any) applies to a
/// request, the partition key that isolates it from other clients, and the permit/window parameters.
/// </summary>
/// <remarks>
/// A plain result type (rather than the real <c>System.Threading.RateLimiting.RateLimitPartition&lt;string&gt;</c>)
/// so partition selection can be unit tested by inspecting a value, without spinning up a
/// <c>PartitionedRateLimiter</c> or the ASP.NET Core pipeline. <see cref="RateLimitingExtensions"/> maps this to
/// the real partition type used by <c>PartitionedRateLimiter.Create</c>.
/// </remarks>
/// <param name="Kind">Which limiter (if any) applies.</param>
/// <param name="PartitionKey">
/// The key isolating this client from others. Deliberately includes the permit limit (e.g. "auth:{id}:{limit}"),
/// because <c>PartitionedRateLimiter.Create</c> caches limiter instances per partition key: without the limit baked
/// into the key, a Service Setting change would not affect a partition that already has a cached limiter instance.
/// </param>
/// <param name="PermitLimit">The maximum number of requests allowed per window. Ignored when <see cref="Kind"/> is <see cref="RateLimitPartitionKind.NoLimiter"/>.</param>
/// <param name="Window">The rate limiting window duration. Ignored when <see cref="Kind"/> is <see cref="RateLimitPartitionKind.NoLimiter"/>.</param>
public sealed record RateLimitDecision(RateLimitPartitionKind Kind, string PartitionKey, int PermitLimit, TimeSpan Window)
{
    /// <summary>
    /// Builds a "no limiter applies" decision. The partition key is irrelevant to behaviour (no throttling occurs)
    /// but a constant is used so every unlimited request shares one partition rather than growing the limiter's
    /// internal partition table unboundedly.
    /// </summary>
    public static RateLimitDecision NoLimiter() => new(RateLimitPartitionKind.NoLimiter, "no-limit", 0, TimeSpan.Zero);
}
