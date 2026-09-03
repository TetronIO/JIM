// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Application.Services;

/// <summary>
/// Run Profile Safeguards (#1618): a thread-safe, in-memory ledger of how many Pending Exports of
/// each change type an Export run may attempt against the Connected System, built once per run from
/// the Run Profile's Max creates/updates/deletes limits and shared by both export passes (the first,
/// immediate pass and the deferred-reference pass) and both connector shapes (calls and files).
/// </summary>
/// <remarks>
/// Pure and dependency-free by design: no database access, no logging, nothing that needs mocking to
/// test. A caller that reserves against an exhausted limit is granted nothing for the shortfall; it is
/// the caller's responsibility to leave whatever it could not attempt exactly as found (Pending,
/// unmarked, given no execution item), never to fail or retry it.
/// </remarks>
public sealed class ExportChangeLimitLedger
{
    private readonly object _lock = new();
    private readonly Dictionary<PendingExportChangeType, int?> _limits;
    private readonly Dictionary<PendingExportChangeType, int> _attempted = new();
    private readonly Dictionary<PendingExportChangeType, int> _withheld = new();

    /// <param name="maxCreates">The Run Profile's Max creates limit, or null for no limit.</param>
    /// <param name="maxUpdates">The Run Profile's Max updates limit, or null for no limit.</param>
    /// <param name="maxDeletes">The Run Profile's Max deletes limit, or null for no limit.</param>
    public ExportChangeLimitLedger(int? maxCreates, int? maxUpdates, int? maxDeletes)
    {
        _limits = new Dictionary<PendingExportChangeType, int?>
        {
            [PendingExportChangeType.Create] = maxCreates,
            [PendingExportChangeType.Update] = maxUpdates,
            [PendingExportChangeType.Delete] = maxDeletes
        };
    }

    /// <summary>
    /// Reserves capacity for up to <paramref name="requested"/> attempts of <paramref name="type"/>,
    /// atomically. Returns how many of them may actually be attempted: all of <paramref name="requested"/>
    /// when the type carries no limit, otherwise the lesser of what was requested and what capacity
    /// remains under the limit. The granted count is added to <see cref="Attempted"/>; any shortfall is
    /// added to <see cref="Withheld"/>.
    /// </summary>
    /// <param name="type">The change type being reserved.</param>
    /// <param name="requested">How many of this type the caller wants to attempt.</param>
    /// <returns>How many of the requested attempts are granted (0 to <paramref name="requested"/>).</returns>
    public int Reserve(PendingExportChangeType type, int requested)
    {
        if (requested <= 0)
            return 0;

        lock (_lock)
        {
            var limit = _limits[type];
            var granted = requested;

            if (limit.HasValue)
            {
                var remaining = Math.Max(0, limit.Value - _attempted.GetValueOrDefault(type));
                granted = Math.Min(requested, remaining);
            }

            _attempted[type] = _attempted.GetValueOrDefault(type) + granted;

            var shortfall = requested - granted;
            if (shortfall > 0)
                _withheld[type] = _withheld.GetValueOrDefault(type) + shortfall;

            return granted;
        }
    }

    /// <summary>How many of <paramref name="type"/> have been withheld so far this run.</summary>
    public int Withheld(PendingExportChangeType type)
    {
        lock (_lock)
        {
            return _withheld.GetValueOrDefault(type);
        }
    }

    /// <summary>How many of <paramref name="type"/> have been granted (attempted) so far this run.</summary>
    public int Attempted(PendingExportChangeType type)
    {
        lock (_lock)
        {
            return _attempted.GetValueOrDefault(type);
        }
    }

    /// <summary>The configured limit for <paramref name="type"/>, or null when this run has no limit for it.</summary>
    public int? Limit(PendingExportChangeType type) => _limits[type];

    /// <summary>True once any change type has withheld at least one export this run.</summary>
    public bool AnyWithheld
    {
        get
        {
            lock (_lock)
            {
                return _withheld.Values.Any(count => count > 0);
            }
        }
    }
}
