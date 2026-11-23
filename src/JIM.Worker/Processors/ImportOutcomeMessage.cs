// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Models.Localisation;
using JIM.Models.Staging;

namespace JIM.Worker.Processors;

/// <summary>
/// The sentence a finished import leaves on its Activity, once the run has nothing left to narrate.
/// The import counterpart of <see cref="ExportOutcomeMessage"/>, kept as a type of its own for the same
/// reason: the wording and the number formatting are pinned by tests without driving a whole import.
/// </summary>
internal static class ImportOutcomeMessage
{
    /// <summary>
    /// The parentheses describe only the objects the run read, each counted once as created, updated, unchanged or
    /// deleted (#2129), so they never add up to more than the run read; the errors are counted alongside. Deletions a
    /// Full Import detected are of objects it did not read, so they follow the parentheses as a clause of their own.
    /// </summary>
    /// <param name="objects">Everything the run read from the Connected System.</param>
    /// <param name="created">Connected System Objects created.</param>
    /// <param name="updated">
    /// Existing Connected System Objects the run changed: at least one attribute value, or an object JIM provisioned
    /// now confirmed as present in the Connected System.
    /// </param>
    /// <param name="unchanged">
    /// Objects read that already matched what JIM held, found so by their content hash or by comparing them, and
    /// deletes the Connector reported for objects JIM no longer held or already held as deleted.
    /// </param>
    /// <param name="connectorReportedDeletions">Deletes the Connector reported for objects JIM held, now marked as deleted.</param>
    /// <param name="detectedDeletions">
    /// Objects a run that read every object in its scope no longer found and newly marked as deleted (a Full Import, or a
    /// Delta Import whose Connector read everything instead of its changes, #2720); not among <paramref name="objects"/>.
    /// </param>
    /// <param name="errors">Execution items the run recorded an error on.</param>
    /// <param name="throughput">
    /// How long the run took and what it averaged, already formatted by
    /// <see cref="ThroughputTracker.FormatCompletion"/>, or empty where there was too little work
    /// to average.
    /// </param>
    /// <param name="withheldDeletions">
    /// Deletes the Connector reported that a Delta Import withheld because they exceeded the Run Profile's deletion limits
    /// (#2089); among <paramref name="objects"/>, and left as JIM held them.
    /// </param>
    internal static string ForImport(int objects, int created, int updated, int unchanged, int connectorReportedDeletions, int detectedDeletions, int errors, string throughput,
        int withheldDeletions = 0)
    {
        return ActivityMessage.ImportComplete(objects, created, updated, unchanged, connectorReportedDeletions, detectedDeletions, errors, throughput,
            withheldDeletions).ToEnglish();
    }

    /// <summary>
    /// Run Profile Safeguards (#1618, Layer 2): the sentence appended to the Activity warning of a run
    /// that read every object in its scope when deletion detection refused because it would have newly
    /// marked more Connected System Objects as deleted than the Run Profile's <c>MaxDetectedDeletions</c>
    /// and/or <c>MaxDetectedDeletionsPercent</c> limit allows. A refused detection marks nothing; the objects
    /// the import did see are still created and updated as normal. A Full Import is run again once the
    /// cause is dealt with; a Delta Import that read every object (#2720) kept its watermark, so each Delta
    /// Import reads everything and checks again by itself.
    /// </summary>
    /// <param name="count">How many Connected System Objects deletion detection would have newly marked as deleted.</param>
    /// <param name="baseCount">How many Connected System Objects were in the run's scope at the start of the run.</param>
    /// <param name="maxCount">The Run Profile's <c>MaxDetectedDeletions</c> limit, or null when unset.</param>
    /// <param name="maxPercent">The Run Profile's <c>MaxDetectedDeletionsPercent</c> limit, or null when unset.</param>
    /// <param name="runType">The Run Profile's type: a Full Import, or a Delta Import that read every object.</param>
    internal static string ForRefusedDeletionDetection(int count, int baseCount, int? maxCount, int? maxPercent, ConnectedSystemRunType runType = ConnectedSystemRunType.FullImport)
    {
        return ActivityMessage.RefusedDeletionDetection(count, baseCount, maxCount, maxPercent, runType).ToEnglish();
    }

    /// <summary>
    /// Run Profile Safeguards on a Delta Import (#2089): the sentence appended to the Activity's warning when the deletes
    /// the Connector reported would have newly marked more Connected System Objects as deleted than the Run Profile's
    /// <c>MaxDetectedDeletions</c> and/or <c>MaxDetectedDeletionsPercent</c> limit allows. None was applied, and the run
    /// kept its starting watermark, so every following Delta Import reports the same deletes until someone acts.
    /// </summary>
    /// <param name="count">How many Connected System Objects the reported deletes would have newly marked as deleted.</param>
    /// <param name="baseCount">How many Connected System Objects were in the run's scope before it saved anything.</param>
    /// <param name="maxCount">The Run Profile's <c>MaxDetectedDeletions</c> limit, or null when unset.</param>
    /// <param name="maxPercent">The Run Profile's <c>MaxDetectedDeletionsPercent</c> limit, or null when unset.</param>
    internal static string ForWithheldConnectorReportedDeletes(int count, int baseCount, int? maxCount, int? maxPercent)
    {
        return ActivityMessage.WithheldConnectorReportedDeletes(count, baseCount, maxCount, maxPercent).ToEnglish();
    }

    /// <summary>
    /// Why a deselected Object Type's objects were left out of deletion detection (#1474): an enabled
    /// Synchronisation Rule is still bound to it, which saving now refuses but an older configuration can still hold.
    /// One sentence group per Object Type, each naming the rules to disable, because that is the fix.
    /// </summary>
    /// <param name="heldBack">Each held-back Object Type's name and the names of the enabled rules bound to it.</param>
    internal static string ForDeselectedObjectTypesHeldBack(IReadOnlyList<(string ObjectTypeName, IReadOnlyList<string> SyncRuleNames)> heldBack)
    {
        return ActivityMessage.DeselectedObjectTypesHeldBack(heldBack)?.ToEnglish() ?? string.Empty;
    }

    /// <summary>
    /// The error a Run Profile Execution Item carries when the import left references on its object unresolved
    /// (#959). Every unresolved value is counted and broken down per attribute, and each cause present gets its own
    /// remedy. It replaces a message that named the first value and said "there may be more", which left the
    /// administrator to find the rest and gave no idea how many there were.
    /// </summary>
    /// <param name="references">
    /// Every reference value the object left unresolved: the attribute it is on, the value, and, where the value
    /// matched objects of more than one Object Type, the names of those types (null where it matched nothing).
    /// Must not be empty.
    /// </param>
    internal static string ForUnresolvedReferences(IReadOnlyList<(string AttributeName, string Value, IReadOnlyList<string>? AmbiguousWith)> references)
    {
        return ActivityMessage.UnresolvedReferences(references).ToEnglish();
    }

    /// <summary>
    /// The error a Run Profile Execution Item carries when the Connector found values on its object that it could not
    /// read as their attributes' types (#2066). Every value is accounted for, and the message says what JIM did about
    /// them: left each attribute as it already held it, rather than treating a value it could not read as no value.
    /// </summary>
    /// <param name="values">Every value the Connector could not read, in the order it reported them. Must not be empty.</param>
    /// <remarks>
    /// JIM reports values too (#2153): two dates less than a microsecond apart, or both true and false. Those were read,
    /// so where any value is JIM's the message says it couldn't be imported, never that it couldn't be read.
    /// </remarks>
    internal static string ForUnreadableAttributeValues(IReadOnlyList<ConnectedSystemImportObjectAttributeValueError> values)
    {
        return ActivityMessage.UnreadableAttributeValues(values).ToEnglish();
    }

    /// <summary>
    /// Why a run that read every object did not check an Object Type's objects for deletion (#2066): the External ID of at
    /// least one of its objects could not be read, so JIM could not tell which object it was, and would otherwise have
    /// found the object it holds for it missing and marked it deleted although the Connected System still holds it. A Delta
    /// Import that read every object (#2720) kept its watermark, so each Delta Import reads everything and checks again.
    /// </summary>
    /// <param name="heldBack">Each held-back Object Type's name and how many of its objects had an unreadable External ID.</param>
    /// <param name="runType">The Run Profile's type: a Full Import, or a Delta Import that read every object.</param>
    internal static string ForUnreadableExternalIds(IReadOnlyList<(string ObjectTypeName, int Count)> heldBack, ConnectedSystemRunType runType = ConnectedSystemRunType.FullImport)
    {
        return ActivityMessage.UnreadableExternalIds(heldBack, runType)?.ToEnglish() ?? string.Empty;
    }

    /// <summary>
    /// #3013: the Activity's warning when deletion detection was held back because objects arrived with no External ID.
    /// </summary>
    internal static string ForMissingExternalIds(IReadOnlyList<(string ObjectTypeName, int Count)> heldBack, ConnectedSystemRunType runType = ConnectedSystemRunType.FullImport)
    {
        return ActivityMessage.MissingExternalIds(heldBack, runType)?.ToEnglish() ?? string.Empty;
    }

    /// <summary>#3013: the item's error for an imported object with no External ID value.</summary>
    internal static string ForMissingExternalId(string externalIdAttributeName) =>
        ActivityMessage.MissingExternalId(externalIdAttributeName).ToEnglish();
}
