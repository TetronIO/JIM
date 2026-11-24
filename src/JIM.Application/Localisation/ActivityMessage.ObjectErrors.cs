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
    /// <param name="comparison">The matching rule's comparison in English, or null where the rule is not known.</param>
    /// <param name="values">The values matched on, already quoted, and how many more there were beyond them.</param>
    /// <param name="previewing">Whether a preview describes what a synchronisation would do.</param>
    public static JimMessage ProvisioningAmbiguity(int matches, string connectedSystemName, string? comparison,
        IReadOnlyList<string> values, int moreValues, bool previewing)
    {
        var outcome = Create(previewing ? nameof(ActivityMessages.ProvisioningOutcomeWouldNot) : nameof(ActivityMessages.ProvisioningOutcomeWasNot));
        if (comparison == null)
            return Create(nameof(ActivityMessages.ProvisioningAmbiguity), Plain(matches), connectedSystemName, outcome);

        var parts = values.Select(value => Create(nameof(ActivityMessages.Verbatim), value)).ToList();
        if (moreValues > 0)
            parts.Add(Create(nameof(ActivityMessages.ProvisioningAmbiguityMoreValues), Plain(moreValues)));

        return Create(nameof(ActivityMessages.ProvisioningAmbiguityOnRule), Plain(matches), connectedSystemName, comparison,
            Listed(parts) ?? Create(nameof(ActivityMessages.Verbatim), string.Empty), outcome);
    }
}
