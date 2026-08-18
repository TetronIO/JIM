// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Processors;

/// <summary>
/// The sentence a finished export leaves on its Activity, once the run has nothing left to narrate.
/// </summary>
/// <remarks>
/// A small type of its own so the wording and the number formatting are pinned by tests: built
/// inline in the processor they could not be reached without driving a whole export, so "10000
/// succeeded" sat there beside an already-grouped throughput figure, formatting its numbers two
/// different ways in one sentence.
/// </remarks>
internal static class ExportOutcomeMessage
{
    /// <param name="throughput">
    /// How long the run took and what it averaged, already formatted by
    /// <see cref="ThroughputTracker.FormatCompletion"/>, or empty where there was too little work
    /// to average.
    /// </param>
    /// <param name="writtenInPart">
    /// How many of the succeeded exports were written in part and are still waiting on a reference
    /// (issue #1398); named in the sentence so "succeeded" does not read as "finished".
    /// </param>
    internal static string ForExport(int succeeded, int failed, int deferred, string throughput, int writtenInPart = 0) =>
        writtenInPart > 0
            ? $"Export complete: {succeeded:N0} succeeded ({writtenInPart:N0} written in part, awaiting references), {failed:N0} failed, {deferred:N0} deferred{throughput}"
            : $"Export complete: {succeeded:N0} succeeded, {failed:N0} failed, {deferred:N0} deferred{throughput}";

    internal static string ForPreview(int pendingExports) =>
        $"Preview complete: {pendingExports:N0} export(s) would be processed";
}
