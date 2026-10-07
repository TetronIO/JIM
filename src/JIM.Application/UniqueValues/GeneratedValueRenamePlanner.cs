// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Application.UniqueValues;

/// <summary>
/// What allowing the rename of a held generated value would do (Unique Value Generation, #242, release 4, Phase 9; plan
/// decision 11: the confirmation names every system that will change). Pure: the caller loads the participants and the
/// object's accounts.
/// </summary>
public static class GeneratedValueRenamePlanner
{
    /// <summary>How far the likely next value is looked for before giving up: a value further along is not "likely".</summary>
    private const int LikelyValueSearchLimit = 1000;

    /// <summary>
    /// Every Connected System the value is exported to where the object has a live account, in the participants' order,
    /// once each: an account being provisioned is created with the new value; one holding the value (compared
    /// case-insensitively, as uniqueness is) is renamed; any other is updated. A system where the object has no account,
    /// or only an obsolete one, changes nothing and is not named.
    /// </summary>
    /// <param name="participants">Where the value is exported, and as which attribute (excluded systems included: an
    /// exclusion only skips the availability check, the value is still exported there).</param>
    /// <param name="accounts">The Connected System Objects of the object the value belongs to, with their attribute values.</param>
    /// <param name="value">The held value.</param>
    public static List<GeneratedValueRenameChange> Plan(
        IReadOnlyCollection<GeneratedValueParticipant> participants, IReadOnlyCollection<ConnectedSystemObject> accounts, string value)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(accounts);

        var changes = new List<GeneratedValueRenameChange>();
        foreach (var system in participants.GroupBy(p => p.ConnectedSystemId))
        {
            var attributeIds = system.Select(p => p.ConnectedSystemObjectTypeAttributeId).ToHashSet();
            var live = accounts.Where(a => a.ConnectedSystemId == system.Key && a.Status != ConnectedSystemObjectStatus.Obsolete).ToList();
            if (live.Count == 0)
                continue;

            var kind = live.Any(a => a.Status == ConnectedSystemObjectStatus.PendingProvisioning)
                ? GeneratedValueRenameChangeKind.Create
                : live.Any(a => a.AttributeValues.Any(av => attributeIds.Contains(av.AttributeId) && string.Equals(av.StringValue, value, StringComparison.OrdinalIgnoreCase)))
                    ? GeneratedValueRenameChangeKind.Rename
                    : GeneratedValueRenameChangeKind.Update;

            changes.Add(new GeneratedValueRenameChange
            {
                ConnectedSystemId = system.Key,
                ConnectedSystemName = system.First().ConnectedSystemName,
                Kind = kind
            });
        }

        return changes;
    }

    /// <summary>
    /// The value JIM is likely to choose next for <paramref name="currentValue"/>: the candidate after it in the
    /// only-if-taken series built from <paramref name="baseValue"/>, which is what Collision Remediation draws from
    /// (gates permitting). Null when that cannot be said cheaply: no base, a sequence or random token, or a current value
    /// that is not from the series.
    /// </summary>
    public static string? LikelyNextValue(SyncRuleMappingGeneration generation, string? baseValue, string currentValue)
    {
        ArgumentNullException.ThrowIfNull(generation);

        if (generation.TokenKind != GeneratedValueTokenKind.OnlyIfTaken || string.IsNullOrEmpty(baseValue))
            return null;

        for (var attempt = 0; attempt < LikelyValueSearchLimit; attempt++)
        {
            var candidate = UniqueValueCandidates.OnlyIfTakenCandidate(baseValue, attempt, generation.SuffixStyle, generation.SuffixStart, generation.Separator);
            if (string.Equals(candidate, currentValue, StringComparison.OrdinalIgnoreCase))
                return UniqueValueCandidates.OnlyIfTakenCandidate(baseValue, attempt + 1, generation.SuffixStyle, generation.SuffixStart, generation.Separator);
        }

        return null;
    }
}
