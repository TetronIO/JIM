// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Utility;

namespace JIM.Worker.Processors;

/// <summary>
/// The sentence a finished import leaves on its Activity, once the run has nothing left to narrate.
/// The import counterpart of <see cref="ExportOutcomeMessage"/>, kept as a type of its own for the same
/// reason: the wording and the number formatting are pinned by tests without driving a whole import.
/// </summary>
internal static class ImportOutcomeMessage
{
    /// <param name="objects">Everything the run read from the Connected System.</param>
    /// <param name="created">Connected System Objects created.</param>
    /// <param name="updated">Connected System Objects whose attributes were changed.</param>
    /// <param name="unchanged">Objects skipped as unchanged by their content hash.</param>
    /// <param name="errors">Objects the run could not import.</param>
    /// <param name="throughput">
    /// How long the run took and what it averaged, already formatted by
    /// <see cref="ThroughputTracker.FormatCompletion"/>, or empty where there was too little work
    /// to average.
    /// </param>
    internal static string ForImport(int objects, int created, int updated, int unchanged, int errors, string throughput)
    {
        var parts = new List<string> { $"{created:N0} created", $"{updated:N0} updated" };
        if (unchanged > 0)
            parts.Add($"{unchanged:N0} unchanged");
        if (errors > 0)
            parts.Add($"{errors:N0} error{(errors == 1 ? "" : "s")}");

        return $"Import complete: {objects:N0} object{(objects == 1 ? "" : "s")} ({string.Join(", ", parts)}){throughput}";
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
            // Defensive: this method is only called once at least one limit has actually tripped, so
            // this arm exists to say something sensible rather than throw if that invariant is ever
            // violated by a future caller.
            limitDescription = "configured limit";

        var objectWord = count == 1 ? "object" : "objects";

        return $"Deletion detection found {count:N0} {objectWord} ({percent}% of {baseCount:N0}) no longer in the Connected System, " +
               $"above this Run Profile's {limitDescription}; none were marked as deleted. Check the Connected System's scope and " +
               "the connector's filters, or raise the limit, then run the Full Import again.";
    }
}
