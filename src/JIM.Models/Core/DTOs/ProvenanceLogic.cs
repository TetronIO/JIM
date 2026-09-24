// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Pure decision logic backing the value provenance inspector (#399): resolving a value's origin, pairing an
/// attribute's raw Add/Remove change rows into display-ready history entries, and the fixed (non-value-dependent)
/// states an Attribute Priority source can be in. Lives in <c>JIM.Models</c> (rather than the more natural
/// <c>JIM.Application</c>) because both <c>MetaverseRepository</c> (the object summary and history queries) and
/// <c>MetaverseServer</c> (the attribute detail orchestration) need it, and the repository layer must not
/// reference the application layer. Kept free of any repository or database dependency either way, so it is
/// unit-testable directly.
/// </summary>
public static class ProvenanceLogic
{
    /// <summary>
    /// Resolves the origin of one Metaverse Object attribute value from its raw contributor columns.
    /// No contributing system recorded means <see cref="ValueOriginKind.NotRecorded"/>; a system recorded but no
    /// contributing rule means the rule has since been deleted (<see cref="ValueOrigin.SyncRuleDeleted"/>), since
    /// <see cref="MetaverseObjectAttributeValue.ContributedBySystemId"/> is retained across rule deletion while
    /// <see cref="MetaverseObjectAttributeValue.ContributedBySyncRuleId"/> is nulled.
    /// </summary>
    public static ValueOrigin ResolveOrigin(
        int? contributedBySystemId,
        int? contributedBySyncRuleId,
        bool assertsNoValue,
        string? connectedSystemName,
        string? syncRuleName)
    {
        if (!contributedBySystemId.HasValue)
        {
            return assertsNoValue
                ? ValueOrigin.NotRecorded with { AssertsNoValue = true }
                : ValueOrigin.NotRecorded;
        }

        return new ValueOrigin
        {
            Kind = ValueOriginKind.SynchronisationRule,
            ConnectedSystemId = contributedBySystemId,
            ConnectedSystemName = connectedSystemName,
            SyncRuleId = contributedBySyncRuleId,
            SyncRuleName = contributedBySyncRuleId.HasValue ? syncRuleName : null,
            SyncRuleDeleted = !contributedBySyncRuleId.HasValue,
            AssertsNoValue = assertsNoValue
        };
    }

    /// <summary>
    /// Resolves the origin of one Metaverse Object change's attribute value (#399: the Changes tab Source column).
    /// Unlike <see cref="ResolveOrigin"/>, a <c>MetaverseObjectChangeAttributeValue</c> carries no
    /// <c>ContributedBySystemId</c> column of its own: the Connected System is resolved by the caller from the
    /// still-live Synchronisation Rule named by <paramref name="contributedBySyncRuleId"/>, so a deleted rule
    /// always resolves the system to null even though <paramref name="contributedBySyncRuleName"/>'s snapshot
    /// survives. Returns null when nothing was ever recorded for this value, so the caller can render nothing
    /// (rather than "Source not recorded") for change rows that pre-date provenance.
    /// </summary>
    public static ValueOrigin? ResolveChangeValueOrigin(
        int? contributedBySyncRuleId,
        string? contributedBySyncRuleName,
        int? contributedBySystemId,
        string? contributedBySystemName)
    {
        if (!contributedBySyncRuleId.HasValue && string.IsNullOrEmpty(contributedBySyncRuleName))
            return null;

        return new ValueOrigin
        {
            Kind = ValueOriginKind.SynchronisationRule,
            ConnectedSystemId = contributedBySystemId,
            ConnectedSystemName = contributedBySystemName,
            SyncRuleId = contributedBySyncRuleId,
            SyncRuleName = contributedBySyncRuleId.HasValue ? contributedBySyncRuleName : null,
            SyncRuleDeleted = !contributedBySyncRuleId.HasValue
        };
    }

    /// <summary>
    /// The state an Attribute Priority source is in before any value has been evaluated for it: null when the
    /// source is joined and of a type worth evaluating (an Attribute or Expression mapping), in which case the
    /// caller evaluates a candidate value and calls <see cref="DetermineUsageState"/> instead.
    /// </summary>
    public static AttributeSourceState? DetermineFixedState(bool joined, SyncRuleMappingSourcesType sourceType)
    {
        if (!joined)
            return AttributeSourceState.NotJoined;

        return sourceType switch
        {
            SyncRuleMappingSourcesType.GeneratedMapping => AttributeSourceState.NotEvaluated,
            SyncRuleMappingSourcesType.AdvancedMapping => AttributeSourceState.NotEvaluated,
            SyncRuleMappingSourcesType.NotSet => AttributeSourceState.NotEvaluated,
            _ => null
        };
    }

    /// <summary>The note shown alongside a fixed <see cref="AttributeSourceState.NotEvaluated"/> state, or null when the source type is evaluated.</summary>
    public static string? NoteForFixedState(SyncRuleMappingSourcesType sourceType) => sourceType switch
    {
        SyncRuleMappingSourcesType.GeneratedMapping => "Generated by JIM",
        SyncRuleMappingSourcesType.AdvancedMapping => "Advanced mapping, not evaluated here",
        SyncRuleMappingSourcesType.NotSet => "No source configured",
        _ => null
    };

    /// <summary>
    /// The state of a source once its candidate value has been evaluated: <see cref="AttributeSourceState.InUse"/>
    /// when its Synchronisation Rule is the current value's contributor, otherwise
    /// <see cref="AttributeSourceState.Outranked"/> when it has a value and <see cref="AttributeSourceState.NoValue"/>
    /// when it does not.
    /// </summary>
    public static AttributeSourceState DetermineUsageState(bool isCurrentContributor, int candidateValueCount)
    {
        if (isCurrentContributor)
            return AttributeSourceState.InUse;

        return candidateValueCount > 0 ? AttributeSourceState.Outranked : AttributeSourceState.NoValue;
    }

    /// <summary>
    /// Pairs an attribute's raw Add/Remove change rows (newest change first; rows from the same change adjacent)
    /// into display-ready <see cref="AttributeHistoryEntry"/> records: a single-valued attribute's Remove-then-Add
    /// within one change becomes one <see cref="AttributeHistoryChangeKind.Set"/> entry, everything else becomes
    /// its own <see cref="AttributeHistoryChangeKind.Added"/> or <see cref="AttributeHistoryChangeKind.Removed"/>
    /// entry. Capped at <paramref name="cap"/> entries; <c>Truncated</c> is true when more entries were produced
    /// than the cap allows (the caller ORs this with whether the raw fetch itself hit its own limit).
    /// </summary>
    public static (List<AttributeHistoryEntry> Entries, bool Truncated) PairAttributeHistory(
        IReadOnlyList<MetaverseAttributeHistoryRawEntry> rawEntriesNewestFirst,
        AttributePlurality plurality,
        int cap)
    {
        var entries = new List<AttributeHistoryEntry>();

        foreach (var group in rawEntriesNewestFirst.GroupBy(r => r.ChangeId))
        {
            var rows = group.ToList();
            var added = rows.FirstOrDefault(r => r.ValueChangeType == ValueChangeType.Add);
            var removed = rows.FirstOrDefault(r => r.ValueChangeType == ValueChangeType.Remove);

            if (plurality == AttributePlurality.SingleValued && rows.Count == 2 && added != null && removed != null)
            {
                entries.Add(new AttributeHistoryEntry
                {
                    Kind = AttributeHistoryChangeKind.Set,
                    Value = added.DisplayValue,
                    PreviousValue = removed.DisplayValue,
                    SyncRuleId = added.SyncRuleId,
                    SyncRuleName = added.SyncRuleName,
                    Change = added.Change
                });
                continue;
            }

            foreach (var row in rows)
            {
                entries.Add(new AttributeHistoryEntry
                {
                    Kind = row.ValueChangeType == ValueChangeType.Add
                        ? AttributeHistoryChangeKind.Added
                        : AttributeHistoryChangeKind.Removed,
                    Value = row.DisplayValue,
                    SyncRuleId = row.SyncRuleId,
                    SyncRuleName = row.SyncRuleName,
                    Change = row.Change
                });
            }
        }

        var truncated = entries.Count > cap;
        return (entries.Take(cap).ToList(), truncated);
    }
}
