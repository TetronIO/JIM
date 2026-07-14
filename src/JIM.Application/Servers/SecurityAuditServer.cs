// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Records authentication (security audit) events onto the Activity system: interactive sign-in success, and
/// aggregated authentication failure (both interactive sign-in and API key). See
/// engineering/plans/done/SECURITY_AUDIT_EVENTS.md for the design this implements.
///
/// Failed authentication is written on behalf of an unauthenticated, attacker-controlled source, so failures are
/// aggregated: one Activity per (API key prefix, client IP, failure reason) per 15-minute UTC window, carrying an
/// attempt counter, so a key-spraying or credential-stuffing client cannot force unbounded database writes. Audit
/// write failures are logged and swallowed here; the authentication outcome this instruments must never be affected
/// by an audit-write failure.
/// </summary>
public class SecurityAuditServer
{
    /// <summary>
    /// The width of the aggregation window failed-authentication attempts are bucketed into.
    /// </summary>
    private static readonly TimeSpan AggregationWindow = TimeSpan.FromMinutes(15);

    private JimApplication Application { get; }

    internal SecurityAuditServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Records a failed authentication attempt (API key or interactive sign-in), aggregating it onto the Activity
    /// for its (API key prefix, client IP, failure reason) 15-minute UTC window: same window bucket increments
    /// <see cref="Activity.AttemptCount"/> and advances <see cref="Activity.LastSeen"/> in place; a new window
    /// bucket creates a fresh, Anonymous-attributed Activity. Never throws: audit-write failures are logged and
    /// swallowed, because the authentication outcome this instruments is primary.
    /// </summary>
    /// <param name="targetName">A short label describing the event class (e.g. "API key authentication failed" or
    /// "Interactive sign-in failed"), used as the created Activity's TargetName. Ignored when the attempt
    /// aggregates onto an existing row.</param>
    /// <param name="reason">The failure reason (e.g. "Invalid API key format", "API key not found").</param>
    /// <param name="apiKeyPrefix">The <c>jim_ak_XXXX</c> prefix of the API key involved, or null when not
    /// applicable (interactive sign-in, or the API key's format was invalid before a prefix could be read).</param>
    /// <param name="clientIp">The client's IP address, or null when unavailable.</param>
    public async Task RecordFailedAuthenticationAsync(string targetName, string reason, string? apiKeyPrefix, string? clientIp)
    {
        try
        {
            await RecordFailedAuthenticationCoreAsync(targetName, reason, apiKeyPrefix, clientIp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "SecurityAuditServer: Failed to record failed authentication event (reason: {Reason})", LogSanitiser.Sanitise(reason));
        }
    }

    /// <summary>
    /// Records a successful interactive sign-in: one completed, User-attributed Activity per call (one per session
    /// establishment), with no aggregation fields set. Never throws: audit-write failures are logged and swallowed,
    /// because the sign-in this instruments has already succeeded and must not be undone by an audit-write failure.
    /// </summary>
    public async Task RecordInteractiveSignInSucceededAsync(MetaverseObject metaverseObject, string? clientIp)
    {
        try
        {
            var activity = new Activity
            {
                TargetType = ActivityTargetType.Authentication,
                TargetOperationType = ActivityTargetOperationType.Authenticate,
                TargetName = "Interactive sign-in succeeded",
                Message = "Interactive sign-in succeeded",
                ClientIpAddress = clientIp
            };

            await Application.Activities.CreateCompletedActivityWithTriadAsync(
                activity, ActivityInitiatorType.User, metaverseObject.Id, metaverseObject.DisplayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "SecurityAuditServer: Failed to record interactive sign-in success event");
        }
    }

    private async Task RecordFailedAuthenticationCoreAsync(string targetName, string reason, string? apiKeyPrefix, string? clientIp)
    {
        var now = DateTime.UtcNow;
        var windowStart = FloorToWindow(now);

        // Postgres unique indexes treat NULLs as distinct from one another, which would defeat deduplication for
        // paths with no known prefix/IP (e.g. a bad-format API key). Normalise to "" so those attempts still
        // aggregate onto a single row instead of one row per attempt.
        var normalisedPrefix = apiKeyPrefix ?? string.Empty;
        var normalisedIp = clientIp ?? string.Empty;

        if (await TryIncrementAsync(normalisedPrefix, normalisedIp, reason, windowStart, now))
            return;

        try
        {
            await CreateAggregatedActivityAsync(targetName, reason, normalisedPrefix, normalisedIp, windowStart, now);
        }
        catch (DbUpdateException)
        {
            // A concurrent caller won the race to create this window's first row (the partial unique index on
            // (TargetType, ApiKeyPrefix, ClientIpAddress, SecurityEventReason, AggregationWindowStart) rejected our
            // insert). Fall back to incrementing the row it created; if that unexpectedly still finds nothing,
            // propagate to the outer catch rather than silently drop the attempt.
            if (!await TryIncrementAsync(normalisedPrefix, normalisedIp, reason, windowStart, now))
                throw;
        }
    }

    private Task<bool> TryIncrementAsync(string apiKeyPrefix, string clientIp, string reason, DateTime windowStart, DateTime now) =>
        Application.Repository.Activity.IncrementAggregatedFailedAuthenticationAsync(apiKeyPrefix, clientIp, reason, windowStart, now);

    private async Task CreateAggregatedActivityAsync(string targetName, string reason, string apiKeyPrefix, string clientIp, DateTime windowStart, DateTime now)
    {
        var activity = new Activity
        {
            TargetType = ActivityTargetType.Authentication,
            TargetOperationType = ActivityTargetOperationType.Authenticate,
            TargetName = targetName,
            Message = targetName,
            ApiKeyPrefix = apiKeyPrefix,
            ClientIpAddress = clientIp,
            SecurityEventReason = reason,
            AggregationWindowStart = windowStart,
            FirstSeen = now,
            LastSeen = now,
            AttemptCount = 1
        };

        // A single terminal insert, deliberately NOT create-then-complete: completing performs a second, full-row
        // update from this caller's in-memory Activity, which would silently erase any AttemptCount increments a
        // concurrent caller lands on the row between the two writes (proven by
        // SecurityAuditServerConcurrencyDatabaseTests against real PostgreSQL).
        await Application.Activities.CreateCompletedActivityWithTriadAsync(activity, ActivityInitiatorType.Anonymous, null, "Anonymous");
    }

    /// <summary>
    /// Floors a UTC timestamp to the start of its 15-minute aggregation window.
    /// </summary>
    private static DateTime FloorToWindow(DateTime utcNow)
    {
        var windowMinutes = (int)AggregationWindow.TotalMinutes;
        var flooredMinute = utcNow.Minute - (utcNow.Minute % windowMinutes);
        return new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, flooredMinute, 0, DateTimeKind.Utc);
    }
}
