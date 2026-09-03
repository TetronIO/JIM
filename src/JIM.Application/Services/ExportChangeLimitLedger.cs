// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Application.Services;

/// <summary>
/// Run Profile Safeguards (#1618): decides, once per Export run, which change types the run is
/// allowed to attempt. Built from the Run Profile's Max creates/updates/deletes limits and the
/// executable Pending Export count per change type at the start of the run
/// (<see cref="Data.Repositories.ISyncRepository.GetExecutableExportCountsByChangeTypeAsync"/>): a
/// type whose pending count exceeds its limit is withheld for the whole run, attempting none of it;
/// a type at or below its limit, or carrying no limit, runs in full. There is no partial attempt and
/// no run-time override: the release valve is a second Export Run Profile without the limit, run by
/// hand.
/// </summary>
/// <remarks>
/// The decision is made once, in the constructor, from the counts the caller supplies; nothing about
/// a type's withheld status changes for the rest of the run, so the ledger is immutable after
/// construction and needs no locking, unlike a running tally would. Shared by both export passes (the
/// first, immediate pass and the deferred-reference pass) and both connector shapes (calls and
/// files): every hook point still calls <see cref="Reserve"/> before attempting anything, so a
/// withheld type is dropped wherever it is met, and the paging query is additionally told to exclude
/// it outright so it is never read into a batch at all (see
/// <see cref="Data.Repositories.ISyncRepository.GetExecutableExportBatchAsync"/>'s
/// <c>excludedChangeTypes</c>).
/// </remarks>
public sealed class ExportChangeLimitLedger
{
    private readonly Dictionary<PendingExportChangeType, int?> _limits;
    private readonly Dictionary<PendingExportChangeType, int> _pendingCounts;
    private readonly HashSet<PendingExportChangeType> _withheldTypes;

    /// <param name="maxCreates">The Run Profile's Max creates limit, or null for no limit.</param>
    /// <param name="maxUpdates">The Run Profile's Max updates limit, or null for no limit.</param>
    /// <param name="maxDeletes">The Run Profile's Max deletes limit, or null for no limit.</param>
    /// <param name="executablePendingCountsByType">The executable Pending Export count per change
    /// type at the start of the run. A type absent from the dictionary is treated as zero pending.</param>
    public ExportChangeLimitLedger(int? maxCreates, int? maxUpdates, int? maxDeletes,
        IReadOnlyDictionary<PendingExportChangeType, int> executablePendingCountsByType)
    {
        ArgumentNullException.ThrowIfNull(executablePendingCountsByType);

        _limits = new Dictionary<PendingExportChangeType, int?>
        {
            [PendingExportChangeType.Create] = maxCreates,
            [PendingExportChangeType.Update] = maxUpdates,
            [PendingExportChangeType.Delete] = maxDeletes
        };

        _pendingCounts = new Dictionary<PendingExportChangeType, int>
        {
            [PendingExportChangeType.Create] = executablePendingCountsByType.GetValueOrDefault(PendingExportChangeType.Create),
            [PendingExportChangeType.Update] = executablePendingCountsByType.GetValueOrDefault(PendingExportChangeType.Update),
            [PendingExportChangeType.Delete] = executablePendingCountsByType.GetValueOrDefault(PendingExportChangeType.Delete)
        };

        // Decided once: a limited type whose pending count exceeds its limit is withheld for the
        // whole run. A limit of 0 withholds a type the moment anything of it is pending, since any
        // positive count already exceeds 0. A type at or under its limit, or with no limit at all,
        // is not withheld, however large its count.
        _withheldTypes = _limits
            .Where(limit => limit.Value.HasValue && _pendingCounts[limit.Key] > limit.Value.Value)
            .Select(limit => limit.Key)
            .ToHashSet();
    }

    /// <summary>
    /// Whether <paramref name="type"/> is withheld for this run: it carries a limit that its pending
    /// count at the start of the run exceeded.
    /// </summary>
    public bool IsWithheld(PendingExportChangeType type) => _withheldTypes.Contains(type);

    /// <summary>
    /// The per-hook-point guard every pass and every connector shape calls before attempting
    /// anything of <paramref name="type"/>: grants the whole of <paramref name="requested"/> for an
    /// allowed type, or 0 for a withheld one. Kept as a method, rather than inlining
    /// <see cref="IsWithheld"/> at each call site, so every hook point (the immediate batch loop, the
    /// files path, the deferred pass) shares one guard.
    /// </summary>
    /// <param name="type">The change type being attempted.</param>
    /// <param name="requested">How many of this type the caller wants to attempt.</param>
    /// <returns><paramref name="requested"/> when <paramref name="type"/> is allowed; 0 when withheld.</returns>
    public int Reserve(PendingExportChangeType type, int requested)
    {
        if (requested <= 0)
            return 0;

        return IsWithheld(type) ? 0 : requested;
    }

    /// <summary>
    /// How many of <paramref name="type"/> are withheld: the executable count pending at the start
    /// of the run for a withheld type (a fixed figure decided once, not a running tally of what has
    /// been dropped so far), or 0 when the type is not withheld.
    /// </summary>
    public int Withheld(PendingExportChangeType type) => IsWithheld(type) ? _pendingCounts[type] : 0;

    /// <summary>The configured limit for <paramref name="type"/>, or null when this run has no limit for it.</summary>
    public int? Limit(PendingExportChangeType type) => _limits[type];

    /// <summary>True when any change type is withheld this run.</summary>
    public bool AnyWithheld => _withheldTypes.Count > 0;
}
