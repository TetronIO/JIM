// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Models.Localisation;
using JIM.Models.Staging;
using JIM.Models.Utility;

namespace JIM.Application.Localisation;

/// <summary>
/// What a finished import says on its Activity and on its execution items, as messages (#3039): the summary, the warnings
/// about refused deletion detection and the errors on an object. The Worker's <c>ImportOutcomeMessage</c> gives each one's
/// British English, which is what these say.
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>The summary a finished import leaves on its Activity.</summary>
    /// <param name="objects">Everything the run read from the Connected System.</param>
    /// <param name="created">Connected System Objects created.</param>
    /// <param name="updated">Existing Connected System Objects the run changed.</param>
    /// <param name="unchanged">Objects read that already matched what JIM held.</param>
    /// <param name="connectorReportedDeletions">Deletes the Connector reported for objects JIM held.</param>
    /// <param name="detectedDeletions">Objects a run that read every object no longer found and newly marked as deleted.</param>
    /// <param name="errors">Execution items the run recorded an error on.</param>
    /// <param name="throughput">How long the run took and what it averaged, already worded, or empty.</param>
    /// <param name="withheldDeletions">Deletes the Connector reported that a Delta Import withheld.</param>
    public static JimMessage ImportComplete(int objects, int created, int updated, int unchanged, int connectorReportedDeletions,
        int detectedDeletions, int errors, MessageArgument throughput, int withheldDeletions = 0)
    {
        var parts = new List<JimMessage>
        {
            Create(nameof(ActivityMessages.ImportCreated), created),
            Create(nameof(ActivityMessages.ImportUpdated), updated)
        };
        if (unchanged > 0)
            parts.Add(Create(nameof(ActivityMessages.ImportUnchanged), unchanged));
        if (connectorReportedDeletions > 0)
            parts.Add(Create(nameof(ActivityMessages.ImportDeleted), connectorReportedDeletions));
        if (withheldDeletions > 0)
            parts.Add(Counted(nameof(ActivityMessages.ImportWithheldOne), withheldDeletions, withheldDeletions));
        if (errors > 0)
            parts.Add(Counted(nameof(ActivityMessages.ImportErrorsOne), errors, errors));

        MessageArgument detected = detectedDeletions > 0
            ? Create(nameof(ActivityMessages.ImportDetectedDeletions), detectedDeletions)
            : string.Empty;

        return Counted(nameof(ActivityMessages.ImportCompleteOne), objects, objects, Listed(parts)!, throughput, detected);
    }

    /// <summary>The Activity warning when deletion detection refused to mark objects deleted.</summary>
    public static JimMessage RefusedDeletionDetection(int count, int baseCount, int? maxCount, int? maxPercent, ConnectedSystemRunType runType)
    {
        var (percent, limits) = DeletionLimits(count, baseCount, maxCount, maxPercent);
        var whatNext = runType == ConnectedSystemRunType.DeltaImport
            ? Create(nameof(ActivityMessages.DeletionDetectionNextDelta))
            : Create(nameof(ActivityMessages.DeletionDetectionNextFull));

        return Counted(nameof(ActivityMessages.DeletionDetectionRefusedOne), count, count, Plain(percent), baseCount, limits, whatNext);
    }

    /// <summary>The Activity warning when a Delta Import withheld the deletes its Connector reported.</summary>
    public static JimMessage WithheldConnectorReportedDeletes(int count, int baseCount, int? maxCount, int? maxPercent)
    {
        var (percent, limits) = DeletionLimits(count, baseCount, maxCount, maxPercent);
        return Counted(nameof(ActivityMessages.DeletesWithheldOne), count, count, Plain(percent), baseCount, limits);
    }

    private static (int Percent, JimMessage Limits) DeletionLimits(int count, int baseCount, int? maxCount, int? maxPercent)
    {
        var percent = baseCount <= 0 ? 0 : (int)Math.Round(count * 100.0 / baseCount, MidpointRounding.AwayFromZero);

        var countTripped = maxCount.HasValue && count > maxCount.Value;
        var percentTripped = maxPercent.HasValue && ShareThreshold.Exceeds(count, baseCount, maxPercent.Value);

        JimMessage limits;
        if (countTripped && percentTripped)
            limits = Create(nameof(ActivityMessages.LimitBoth), maxCount!.Value, Plain(maxPercent!.Value));
        else if (countTripped)
            limits = Create(nameof(ActivityMessages.LimitCount), maxCount!.Value);
        else if (percentTripped)
            limits = Create(nameof(ActivityMessages.LimitPercent), Plain(maxPercent!.Value));
        else
            // Defensive: only called once a limit has tripped; say something sensible rather than throw if a caller breaks that.
            limits = Create(nameof(ActivityMessages.LimitConfigured));

        return (percent, limits);
    }

    /// <summary>Why a deselected Object Type's objects were left out of deletion detection, one sentence group per Object Type.</summary>
    public static JimMessage? DeselectedObjectTypesHeldBack(IReadOnlyList<(string ObjectTypeName, IReadOnlyList<string> SyncRuleNames)> heldBack) =>
        Spaced(heldBack.Select(item =>
        {
            var rules = string.Join(", ", item.SyncRuleNames);
            var count = item.SyncRuleNames.Count;
            var because = Counted(nameof(ActivityMessages.DeselectedBecauseOne), count, count, rules);
            var disable = Counted(nameof(ActivityMessages.DeselectedDisableOne), count);
            return Create(nameof(ActivityMessages.DeselectedHeldBack), item.ObjectTypeName, because, disable);
        }));

    /// <summary>The error an execution item carries when the import left references on its object unresolved.</summary>
    public static JimMessage UnresolvedReferences(IReadOnlyList<(string AttributeName, string Value, IReadOnlyList<string>? AmbiguousWith)> references)
    {
        // Resolution runs in parallel, so the order values arrive in is not stable; sorting keeps the message, and the
        // value it gives as an example, the same from one run to the next.
        var ordered = references
            .OrderBy(r => r.AttributeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Value, StringComparer.Ordinal)
            .ToList();
        var missing = ordered.Where(r => r.AmbiguousWith == null).ToList();
        var ambiguous = ordered.Where(r => r.AmbiguousWith != null).ToList();
        var candidateTypeNames = string.Join(", ", ambiguous
            .SelectMany(r => r.AmbiguousWith!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        if (ordered.Count == 1)
        {
            var (attributeName, value, _) = ordered[0];
            return missing.Count == 1
                ? Create(nameof(ActivityMessages.UnresolvedSingleMissing), attributeName, value, ScopeRemedy(1))
                : Create(nameof(ActivityMessages.UnresolvedSingleAmbiguous), value, candidateTypeNames, attributeName,
                    Create(nameof(ActivityMessages.UnresolvedSchemaRemedy)));
        }

        var breakdown = Listed(ordered
            .GroupBy(r => r.AttributeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => Create(nameof(ActivityMessages.UnresolvedOnAttribute), group.Count(), group.Key)))!;

        var sentences = new List<JimMessage> { Create(nameof(ActivityMessages.UnresolvedMany), ordered.Count, breakdown) };
        if (missing.Count > 0)
        {
            sentences.Add(ambiguous.Count == 0
                ? Create(nameof(ActivityMessages.UnresolvedExample), missing[0].Value)
                : Counted(nameof(ActivityMessages.UnresolvedMissingOne), missing.Count, missing.Count, missing[0].Value));
            sentences.Add(ScopeRemedy(missing.Count));
        }

        if (ambiguous.Count > 0)
        {
            sentences.Add(missing.Count == 0
                ? Create(nameof(ActivityMessages.UnresolvedAmbiguousAll), candidateTypeNames)
                : Counted(nameof(ActivityMessages.UnresolvedAmbiguousSomeOne), ambiguous.Count, ambiguous.Count, candidateTypeNames));
            sentences.Add(Create(nameof(ActivityMessages.UnresolvedSchemaRemedy)));
        }

        sentences.Add(Create(nameof(ActivityMessages.UnresolvedListsEvery)));
        return Spaced(sentences)!;
    }

    private static JimMessage ScopeRemedy(int count) =>
        Counted(nameof(ActivityMessages.UnresolvedScopeRemedyOne), count);

    /// <summary>How many unreadable values the error lists one by one before it counts the rest.</summary>
    private const int ListedUnreadableValues = 5;

    /// <summary>Longest a value is quoted at in full; anything longer is shortened.</summary>
    private const int LongestQuotedValue = 100;

    /// <summary>The error an execution item carries when the Connector found values it could not read as their attributes' types.</summary>
    public static JimMessage UnreadableAttributeValues(IReadOnlyList<ConnectedSystemImportObjectAttributeValueError> values)
    {
        var verb = values.Any(v => v.Source == ImportAttributeValueErrorSource.Jim)
            ? Create(nameof(ActivityMessages.VerbImport))
            : Create(nameof(ActivityMessages.VerbRead));

        if (values.Count == 1)
        {
            var only = values[0];
            return Create(nameof(ActivityMessages.UnreadableSingleValue), verb, only.AttributeName, QuotedValue(only.Value), AsSentence(only.Reason));
        }

        var breakdown = Listed(values
            .GroupBy(v => v.AttributeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => Create(nameof(ActivityMessages.UnresolvedOnAttribute), group.Count(), group.Key)))!;

        var sentences = new List<JimMessage> { Create(nameof(ActivityMessages.UnreadableValuesMany), verb, values.Count, breakdown) };
        sentences.AddRange(values
            .Take(ListedUnreadableValues)
            .Select(v => Create(nameof(ActivityMessages.Verbatim), $"{v.AttributeName}{QuotedValue(v.Value)}: {AsSentence(v.Reason)}")));
        if (values.Count > ListedUnreadableValues)
            sentences.Add(Create(nameof(ActivityMessages.UnreadableAndMore), values.Count - ListedUnreadableValues));
        sentences.Add(Create(nameof(ActivityMessages.UnreadableCorrectMany)));

        return Spaced(sentences)!;
    }

    /// <summary>Why a run that read every object did not check an Object Type's objects for deletion: the External ID of some could not be read.</summary>
    public static JimMessage? UnreadableExternalIds(IReadOnlyList<(string ObjectTypeName, int Count)> heldBack, ConnectedSystemRunType runType)
    {
        var whatNext = runType == ConnectedSystemRunType.DeltaImport
            ? Create(nameof(ActivityMessages.ExternalIdsNextDelta))
            : Create(nameof(ActivityMessages.ExternalIdsNextFull));

        return Spaced(heldBack.Select(item =>
            Create(nameof(ActivityMessages.ExternalIdsUnreadable), item.ObjectTypeName,
                Counted(nameof(ActivityMessages.ObjectsCountOne), item.Count, item.Count), whatNext)));
    }

    /// <summary>Why deletion detection was held back because objects arrived with no External ID.</summary>
    public static JimMessage? MissingExternalIds(IReadOnlyList<(string ObjectTypeName, int Count)> heldBack, ConnectedSystemRunType runType) =>
        Spaced(heldBack.Select(item =>
        {
            var whatNext = runType == ConnectedSystemRunType.DeltaImport
                ? Counted(nameof(ActivityMessages.ExternalIdsMissingNextDeltaOne), item.Count)
                : Counted(nameof(ActivityMessages.ExternalIdsMissingNextFullOne), item.Count);
            return Counted(nameof(ActivityMessages.ExternalIdsMissingOne), item.Count, item.ObjectTypeName, whatNext, item.Count);
        }));

    /// <summary>The error an execution item carries for an imported object with no External ID value.</summary>
    public static JimMessage MissingExternalId(string externalIdAttributeName) =>
        Create(nameof(ActivityMessages.ExternalIdMissing), externalIdAttributeName);

    private static string QuotedValue(string? value)
    {
        if (value == null)
            return string.Empty;

        var shown = value.Length > LongestQuotedValue ? $"{value[..LongestQuotedValue]}..." : value;
        return $" ('{shown}')";
    }

    private static string AsSentence(string reason)
    {
        var trimmed = reason.Trim();
        return trimmed.EndsWith('.') ? trimmed : $"{trimmed}.";
    }
}
