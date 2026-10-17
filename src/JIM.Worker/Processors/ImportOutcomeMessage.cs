// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Utility;

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
    /// <param name="detectedDeletions">Objects a Full Import no longer found and newly marked as deleted; not among <paramref name="objects"/>.</param>
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
        var parts = new List<string> { $"{created:N0} created", $"{updated:N0} updated" };
        if (unchanged > 0)
            parts.Add($"{unchanged:N0} unchanged");
        if (connectorReportedDeletions > 0)
            parts.Add($"{connectorReportedDeletions:N0} deleted");
        if (withheldDeletions > 0)
            parts.Add($"{withheldDeletions:N0} deletion{(withheldDeletions == 1 ? "" : "s")} withheld");
        if (errors > 0)
            parts.Add($"{errors:N0} error{(errors == 1 ? "" : "s")}");

        var detected = detectedDeletions > 0 ? $"; {detectedDeletions:N0} no longer found and marked as deleted" : string.Empty;

        // The throughput is the objects read over the run's time, so it stays with them: after the detected deletions it
        // would read as the time those took.
        return $"Import complete: {objects:N0} object{(objects == 1 ? "" : "s")} ({string.Join(", ", parts)}){throughput}{detected}";
    }

    /// <summary>
    /// Run Profile Safeguards (#1618, Layer 2): the sentence appended to a Full Import's Activity
    /// warning when deletion detection refused because it would have newly marked more Connected
    /// System Objects as deleted than the Run Profile's <c>MaxDetectedDeletions</c> and/or
    /// <c>MaxDetectedDeletionsPercent</c> limit allows. A refused detection marks nothing; the objects
    /// the import did see are still created and updated as normal.
    /// </summary>
    /// <param name="count">How many Connected System Objects deletion detection would have newly marked as deleted.</param>
    /// <param name="baseCount">How many Connected System Objects were in the run's scope at the start of the run.</param>
    /// <param name="maxCount">The Run Profile's <c>MaxDetectedDeletions</c> limit, or null when unset.</param>
    /// <param name="maxPercent">The Run Profile's <c>MaxDetectedDeletionsPercent</c> limit, or null when unset.</param>
    internal static string ForRefusedDeletionDetection(int count, int baseCount, int? maxCount, int? maxPercent)
    {
        var (percent, limitDescription) = DescribeDeletionLimits(count, baseCount, maxCount, maxPercent);
        var objectWord = count == 1 ? "object" : "objects";

        return $"Deletion detection found {count:N0} {objectWord} ({percent}% of {baseCount:N0}) no longer in the Connected System, " +
               $"above this Run Profile's {limitDescription}; none were marked as deleted. Check the Connected System's scope and " +
               "the connector's filters, or raise the limit, then run the Full Import again.";
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
        var (percent, limitDescription) = DescribeDeletionLimits(count, baseCount, maxCount, maxPercent);
        var objectWord = count == 1 ? "object" : "objects";

        return $"The Connected System reported {count:N0} {objectWord} ({percent}% of {baseCount:N0}) as deleted, above this Run Profile's " +
               $"{limitDescription}; none were marked as deleted. Each Delta Import will report them again until this is resolved. " +
               "Check what deleted them in the Connected System. If the deletions are intended, raise the limit, or run a Delta Import " +
               "Run Profile without these limits to let them through.";
    }

    /// <summary>
    /// The share of <paramref name="baseCount"/> that <paramref name="count"/> is, rounded to a whole percentage, and the
    /// words naming whichever of the Run Profile's deletion limits it exceeds.
    /// </summary>
    private static (int Percent, string LimitDescription) DescribeDeletionLimits(int count, int baseCount, int? maxCount, int? maxPercent)
    {
        var percent = baseCount <= 0 ? 0 : (int)Math.Round(count * 100.0 / baseCount, MidpointRounding.AwayFromZero);

        var countTripped = maxCount.HasValue && count > maxCount.Value;
        var percentTripped = maxPercent.HasValue && ShareThreshold.Exceeds(count, baseCount, maxPercent.Value);

        string limitDescription;
        if (countTripped && percentTripped)
            limitDescription = $"limits of {maxCount!.Value:N0} and {maxPercent!.Value}%";
        else if (countTripped)
            limitDescription = $"limit of {maxCount!.Value:N0}";
        else if (percentTripped)
            limitDescription = $"limit of {maxPercent!.Value}%";
        else
            // Defensive: this is only called once at least one limit has actually tripped, so this arm exists to say
            // something sensible rather than throw if that invariant is ever violated by a future caller.
            limitDescription = "configured limit";

        return (percent, limitDescription);
    }

    /// <summary>
    /// Why a deselected Object Type's objects were left out of deletion detection (#1474): an enabled
    /// Synchronisation Rule is still bound to it, which saving now refuses but an older configuration can still hold.
    /// One sentence group per Object Type, each naming the rules to disable, because that is the fix.
    /// </summary>
    /// <param name="heldBack">Each held-back Object Type's name and the names of the enabled rules bound to it.</param>
    internal static string ForDeselectedObjectTypesHeldBack(IReadOnlyList<(string ObjectTypeName, IReadOnlyList<string> SyncRuleNames)> heldBack)
    {
        return string.Join(" ", heldBack.Select(item =>
        {
            var rules = string.Join(", ", item.SyncRuleNames);
            var because = item.SyncRuleNames.Count == 1
                ? $"an enabled Synchronisation Rule still manages it: {rules}."
                : $"{item.SyncRuleNames.Count} enabled Synchronisation Rules still manage it: {rules}.";
            var disable = item.SyncRuleNames.Count == 1 ? "that Synchronisation Rule" : "those Synchronisation Rules";

            return $"Object Type '{item.ObjectTypeName}' is deselected, but its objects were not checked for deletion because {because} " +
                   $"They stay joined and are no longer refreshed. Disable {disable} to take the Object Type out of management, " +
                   "or select the Object Type again.";
        }));
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
        // Resolution runs in parallel, so the order values arrive in is not stable; sorting here keeps the message,
        // and the value it gives as an example, the same from one run to the next.
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
                ? $"Couldn't resolve a reference on {attributeName} to a Connected System Object: {value}. {ScopeRemedy(1)}"
                : $"A reference value ({value}) matches objects of more than one Object Type ({candidateTypeNames}), and attribute " +
                  $"'{attributeName}' does not declare which Object Type it references, so the reference cannot be resolved. {SchemaRemedy}";
        }

        var breakdown = string.Join(", ", ordered
            .GroupBy(r => r.AttributeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Count():N0} on {group.Key}"));

        var sentences = new List<string> { $"Couldn't resolve {ordered.Count:N0} references to Connected System Objects ({breakdown})." };
        if (missing.Count > 0)
        {
            sentences.Add(ambiguous.Count == 0
                ? $"For example: {missing[0].Value}."
                : $"{missing.Count:N0} {Matches(missing.Count)} no Connected System Object, for example {missing[0].Value}.");
            sentences.Add(ScopeRemedy(missing.Count));
        }

        if (ambiguous.Count > 0)
        {
            var whichMatch = missing.Count == 0 ? "Each matches" : $"{ambiguous.Count:N0} {Matches(ambiguous.Count)}";
            sentences.Add($"{whichMatch} objects of more than one Object Type ({candidateTypeNames}) on an attribute that does not declare " +
                          "which Object Type it references.");
            sentences.Add(SchemaRemedy);
        }

        sentences.Add("The Connected System Object lists every unresolved value.");
        return string.Join(" ", sentences);
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
        var verb = values.Any(v => v.Source == ImportAttributeValueErrorSource.Jim) ? "import" : "read";
        if (values.Count == 1)
        {
            var only = values[0];
            return $"Couldn't {verb} a value of {only.AttributeName}{QuotedValue(only.Value)}: {AsSentence(only.Reason)} " +
                   $"JIM left {only.AttributeName} as it already held it. Correct the value in the Connected System, then run the import again.";
        }

        var breakdown = string.Join(", ", values
            .GroupBy(v => v.AttributeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Count():N0} on {group.Key}"));

        var sentences = new List<string>
        {
            $"Couldn't {verb} {values.Count:N0} values ({breakdown}), so JIM left those attributes as it already held them."
        };
        sentences.AddRange(values
            .Take(ListedUnreadableValues)
            .Select(v => $"{v.AttributeName}{QuotedValue(v.Value)}: {AsSentence(v.Reason)}"));
        if (values.Count > ListedUnreadableValues)
            sentences.Add($"And {values.Count - ListedUnreadableValues:N0} more.");
        sentences.Add("Correct the values in the Connected System, then run the import again.");

        return string.Join(" ", sentences);
    }

    /// <summary>
    /// Why a Full Import did not check an Object Type's objects for deletion (#2066): the External ID of at least one of
    /// its objects could not be read, so JIM could not tell which object it was, and would otherwise have found the
    /// object it holds for it missing and marked it deleted although the Connected System still holds it.
    /// </summary>
    /// <param name="heldBack">Each held-back Object Type's name and how many of its objects had an unreadable External ID.</param>
    internal static string ForUnreadableExternalIds(IReadOnlyList<(string ObjectTypeName, int Count)> heldBack)
    {
        return string.Join(" ", heldBack.Select(item =>
        {
            var objects = item.Count == 1 ? "1 object" : $"{item.Count:N0} objects";
            return $"Object Type '{item.ObjectTypeName}' was not checked for deleted objects, because the External ID of {objects} " +
                   "could not be read and JIM could not tell which objects they were. None of its objects were marked as deleted. " +
                   "Correct the External IDs in the Connected System, then run the Full Import again.";
        }));
    }

    /// <summary>How many unreadable values the message lists one by one before it counts the rest.</summary>
    private const int ListedUnreadableValues = 5;

    /// <summary>Longest a value is quoted at in full; anything longer is shortened, since the item exists to say what is wrong, not to hold a payload.</summary>
    private const int LongestQuotedValue = 100;

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

    /// <summary>
    /// Both ways an object can be out of scope (#1255): its Container was never selected, or a Container excludes it.
    /// The second is the harder to spot, because the branch above the referenced object is selected and looks right.
    /// </summary>
    private static string ScopeRemedy(int count) => count == 1
        ? "Make sure that Container Scope for the Connected System includes the location of the referenced object, and that no Container excludes it."
        : "Make sure that Container Scope for the Connected System includes the locations of the referenced objects, and that no Container excludes them.";

    private const string SchemaRemedy =
        "Declare the reference's target Object Type in the Connected System's schema configuration if the Connector supports doing so.";

    private static string Matches(int count) => count == 1 ? "matches" : "match";
}
