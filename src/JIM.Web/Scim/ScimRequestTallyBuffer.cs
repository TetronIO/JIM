// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;

namespace JIM.Web.Scim;

/// <summary>
/// Gathers SCIM request outcomes per Connected System, key and 15-minute window until <see cref="ScimRequestTallyFlusher"/>
/// drains them onto the summary Activities, so a parallel initial load costs one database write per window per flush
/// rather than one per request (decided 20 October 2026).
/// </summary>
/// <remarks>
/// One lock guards recording and draining, so a request recorded while a drain is under way lands in this drain or the
/// next, never in neither. The lock is held for a dictionary lookup and a few increments, at the endpoint's rate limit
/// of at most a few thousand requests a minute.
/// </remarks>
public class ScimRequestTallyBuffer(TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private Dictionary<(int ConnectedSystemId, Guid ApiKeyId, DateTime WindowStart), Counts> _counts = [];

    /// <summary>
    /// Counts one request. A request that is not counted records nothing.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System the request was for.</param>
    /// <param name="apiKeyId">The key it was made with.</param>
    /// <param name="apiKeyName">The key's name.</param>
    /// <param name="outcome">What it did.</param>
    /// <param name="rejection">For a rejection, how to name it: status, SCIM error type, method and endpoint.</param>
    public void Record(int connectedSystemId, Guid apiKeyId, string apiKeyName, ScimRequestOutcome outcome, string? rejection)
    {
        if (outcome == ScimRequestOutcome.NotCounted)
            return;

        var now = time.GetUtcNow().UtcDateTime;
        var key = (connectedSystemId, apiKeyId, FloorToWindow(now));
        lock (_gate)
        {
            if (!_counts.TryGetValue(key, out var counts))
                _counts[key] = counts = new Counts(apiKeyName, now);

            counts.LastSeen = now;
            switch (outcome)
            {
                case ScimRequestOutcome.Created: counts.Created++; break;
                case ScimRequestOutcome.Updated: counts.Updated++; break;
                case ScimRequestOutcome.Deleted: counts.Deleted++; break;
                case ScimRequestOutcome.Rejected:
                    counts.Rejected++;
                    counts.LatestRejection = rejection ?? counts.LatestRejection;
                    break;
            }
        }
    }

    /// <summary>
    /// Takes everything counted since the last drain, leaving the buffer empty.
    /// </summary>
    public IReadOnlyList<ScimRequestTally> Drain()
    {
        Dictionary<(int ConnectedSystemId, Guid ApiKeyId, DateTime WindowStart), Counts> drained;
        lock (_gate)
        {
            if (_counts.Count == 0)
                return [];
            drained = _counts;
            _counts = [];
        }

        return drained.Select(entry => new ScimRequestTally
        {
            ConnectedSystemId = entry.Key.ConnectedSystemId,
            ApiKeyId = entry.Key.ApiKeyId,
            ApiKeyName = entry.Value.ApiKeyName,
            WindowStart = entry.Key.WindowStart,
            FirstSeen = entry.Value.FirstSeen,
            LastSeen = entry.Value.LastSeen,
            Created = entry.Value.Created,
            Updated = entry.Value.Updated,
            Deleted = entry.Value.Deleted,
            Rejected = entry.Value.Rejected,
            LatestRejection = entry.Value.LatestRejection
        }).ToList();
    }

    private static DateTime FloorToWindow(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % Window.Ticks, DateTimeKind.Utc);

    private sealed class Counts(string apiKeyName, DateTime firstSeen)
    {
        public string ApiKeyName { get; } = apiKeyName;
        public DateTime FirstSeen { get; } = firstSeen;
        public DateTime LastSeen { get; set; } = firstSeen;
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public int Rejected { get; set; }
        public string? LatestRejection { get; set; }
    }
}
