// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Models.Localisation;

namespace JIM.Application.Localisation;

/// <summary>
/// The errors an import, an export and export matching record on a Run Profile Execution Item that were English only after
/// the first conversion (#3084): Object Type changes the import refused, references an export could not write, and the
/// match errors that stop provisioning.
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>A Full Import that allows Object Type changes could not retire the old object, so the new one was not created.</summary>
    public static JimMessage ImportObjectTypeChangeWithdrawn(string heldTypeName, string arrivingTypeName) =>
        Create(nameof(ActivityMessages.ImportObjectTypeChangeWithdrawn), heldTypeName, arrivingTypeName);

    /// <summary>An import saw an object arrive as a different Object Type from the one JIM holds, and refused the change.</summary>
    /// <param name="heldIsObsolete">Whether the object JIM holds was already marked obsolete by an earlier import.</param>
    public static JimMessage ImportObjectTypeChangeRefused(string heldTypeName, string arrivingTypeName, string? externalId, bool heldIsObsolete)
    {
        var kept = heldIsObsolete
            ? Create(nameof(ActivityMessages.ImportKeptHeldObsolete), heldTypeName)
            : Create(nameof(ActivityMessages.ImportKeptHeldNothingDeprovisioned), heldTypeName);
        return Create(nameof(ActivityMessages.ImportObjectTypeChangeRefused), heldTypeName, arrivingTypeName, externalId, kept);
    }

    /// <summary>An import item's error where the Connector's Object Type is not one JIM has a usable schema for.</summary>
    public static JimMessage ImportObjectTypeNotFound(int connectedSystemId, string? objectType) =>
        Create(nameof(ActivityMessages.ImportObjectTypeNotFound), Plain(connectedSystemId), objectType);

    /// <summary>The note on an export that refers to objects the target does not hold.</summary>
    /// <param name="references">The references, each already written as "attribute -> name".</param>
    /// <param name="shown">How many references to list before saying "and N more".</param>
    public static JimMessage UnresolvedReferences(IReadOnlyList<string> references, int shown)
    {
        MessageArgument summary = string.Join("; ", references.Take(shown));
        if (references.Count > shown)
            summary = Create(nameof(ActivityMessages.UnresolvedReferencesAndMore), summary, Plain(references.Count - shown));

        return Create(nameof(ActivityMessages.UnresolvedReferencesNote), Plain(references.Count), summary);
    }

    /// <summary>Export matching could not be worked out for a Metaverse Object before provisioning to a Connected System.</summary>
    public static JimMessage ProvisioningMatchFailed(string connectedSystemName, MessageArgument cause) =>
        Create(nameof(ActivityMessages.ProvisioningMatchFailed), connectedSystemName, cause);

    /// <summary>Export matching found several objects where it needed one.</summary>
    /// <param name="matches">How many Connected System Objects matched.</param>
    /// <param name="comparison">What the matching rule compares, as a message, or null where the rule is not known.</param>
    /// <param name="values">The values matched on, already quoted, and how many more there were beyond them.</param>
    /// <param name="previewing">Whether a preview describes what a synchronisation would do.</param>
    public static JimMessage ProvisioningAmbiguity(int matches, string connectedSystemName, MessageArgument? comparison,
        IReadOnlyList<string> values, int moreValues, bool previewing)
    {
        var outcome = Create(previewing ? nameof(ActivityMessages.ProvisioningOutcomeWouldNot) : nameof(ActivityMessages.ProvisioningOutcomeWasNot));
        if (comparison == null)
            return Create(nameof(ActivityMessages.ProvisioningAmbiguity), Plain(matches), connectedSystemName, outcome);

        return Create(nameof(ActivityMessages.ProvisioningAmbiguityOnRule), Plain(matches), connectedSystemName, comparison,
            MatchedValues(values, moreValues), outcome);
    }

    /// <summary>An import's matching found several Metaverse Objects where it needed one (#3094).</summary>
    /// <param name="matches">How many Metaverse Objects matched.</param>
    /// <param name="comparison">What the matching rule compares, as a message, or null where the rule is not known.</param>
    /// <param name="values">The values matched on, already quoted, and how many more there were beyond them.</param>
    /// <param name="previewing">Whether a preview describes what a synchronisation would do.</param>
    public static JimMessage ImportAmbiguity(int matches, MessageArgument? comparison, IReadOnlyList<string> values, int moreValues, bool previewing)
    {
        var outcome = ImportOutcome(previewing);
        if (comparison == null)
            return Create(nameof(ActivityMessages.ImportAmbiguity), Plain(matches), outcome);

        return Create(nameof(ActivityMessages.ImportAmbiguityOnRule), Plain(matches), comparison, MatchedValues(values, moreValues), outcome);
    }

    /// <summary>An object's import Synchronisation Rules found different Metaverse Objects, and JIM chooses none of them (#3094).</summary>
    /// <param name="differentObjects">How many different Metaverse Objects the rules found.</param>
    /// <param name="found">What each rule found: the rule's name, the Metaverse Object's name, what the rule compares and the value it matched on.</param>
    /// <param name="previewing">Whether a preview describes what a synchronisation would do.</param>
    public static JimMessage ImportRuleDisagreement(int differentObjects,
        IEnumerable<(string SyncRuleName, string MetaverseObjectName, JimMessage Comparison, string MatchedValue)> found, bool previewing)
    {
        var items = found
            .Select(item => Create(nameof(ActivityMessages.ImportRuleDisagreementItem), item.SyncRuleName, item.MetaverseObjectName, item.Comparison, item.MatchedValue))
            .Aggregate((JimMessage?)null, (joined, next) => joined is null ? next : Create(nameof(ActivityMessages.JoinedBySemicolon), joined, next));

        return Create(nameof(ActivityMessages.ImportRuleDisagreement), Plain(differentObjects),
            items ?? Create(nameof(ActivityMessages.Verbatim), string.Empty), ImportOutcome(previewing));
    }

    private static JimMessage ImportOutcome(bool previewing) =>
        Create(previewing ? nameof(ActivityMessages.ImportOutcomeWouldNot) : nameof(ActivityMessages.ImportOutcomeWasNot));

    /// <summary>The values a match was made on, comma separated, with "and n more" last where some were left out.</summary>
    private static JimMessage MatchedValues(IReadOnlyList<string> values, int moreValues)
    {
        var parts = values.Select(value => Create(nameof(ActivityMessages.Verbatim), value)).ToList();
        if (moreValues > 0)
            parts.Add(Create(nameof(ActivityMessages.ProvisioningAmbiguityMoreValues), Plain(moreValues)));

        return Listed(parts) ?? Create(nameof(ActivityMessages.Verbatim), string.Empty);
    }
}
