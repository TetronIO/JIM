// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using JIM.Web.Models;

namespace JIM.Web;

/// <summary>
/// Pure display and decision logic for the "JIM generates it" Attribute Flow form's live preview panel, the
/// sequence state panel, the skip-ahead confirmation and the "Start again" row action (Unique Value Generation,
/// #242, Phase 3, Work Package D2). Kept free of Blazor/MudBlazor, <c>IExpressionEvaluator</c> and
/// <c>JimApplication</c> so it is directly unit-testable; see <c>GeneratedValuePreviewHelpersTests</c>. The
/// component wiring (evaluating the base expression, calling <c>DescribeGeneratedCandidates</c>) lives in
/// <c>GeneratedValuePreviewPanel.razor</c>.
/// </summary>
public static class GeneratedValuePreviewHelpers
{
    /// <summary>
    /// The placeholder value substituted for every input the base expression reads when the administrator has
    /// not entered any sample values in "Test this Expression" (approved mockup: "Preview · sample inputs").
    /// </summary>
    public const string NeutralSampleValue = "Sample";

    /// <summary>The mockup's static explanation for a Sequence token's "If a number is taken" line.</summary>
    public const string SequenceTakenExplanation = "skipped; the next one is used and the skipped number is not reused";

    /// <summary>The mockup's static "Source of randomness" line for a Random token's example.</summary>
    public const string RandomSourceOfRandomness = "cryptographic";

    /// <summary>
    /// Whether the administrator has typed anything into any of "Test this Expression"'s sample inputs.
    /// </summary>
    public static bool HasAnySampleValue(IReadOnlyDictionary<string, string?> testerSampleValues) =>
        testerSampleValues.Values.Any(v => !string.IsNullOrEmpty(v));

    /// <summary>
    /// Builds the Metaverse and Connected System attribute dictionaries an <c>IExpressionEvaluator</c> needs to
    /// evaluate the base expression for the live preview: the administrator's own sample values from "Test this
    /// Expression" when any have been entered (missing inputs read as no value, exactly as the tester itself
    /// evaluates), otherwise every input filled with <see cref="NeutralSampleValue"/> so an expression with no
    /// entered samples still previews something rather than a string built from nulls.
    /// </summary>
    public static GeneratedValueSampleContext BuildSampleContext(
        IReadOnlyList<ExpressionInput> inputs, IReadOnlyDictionary<string, string?> testerSampleValues)
    {
        var metaverse = new Dictionary<string, object?>();
        var connectedSystem = new Dictionary<string, object?>();
        var usedNeutralSample = !HasAnySampleValue(testerSampleValues);

        foreach (var input in inputs)
        {
            object? value;
            if (usedNeutralSample)
            {
                value = NeutralSampleValue;
            }
            else
            {
                var sample = testerSampleValues.GetValueOrDefault(input.Accessor);
                value = string.IsNullOrEmpty(sample) ? null : sample;
            }

            var target = input.Source == ExpressionInputSource.Metaverse ? metaverse : connectedSystem;
            target[input.AttributeName] = value;
        }

        return new GeneratedValueSampleContext(metaverse, connectedSystem, usedNeutralSample);
    }

    /// <summary>
    /// The caption naming what sample values the preview is built from, shown beside the "Preview" heading
    /// (approved mockup screen 01: "Preview · sample inputs Joe Bloggs"). Empty when the base expression reads
    /// no inputs, since there is then nothing to attribute the preview to.
    /// </summary>
    public static string DescribeSampleSource(bool hasInputs, bool usedNeutralSample) =>
        !hasInputs
            ? string.Empty
            : usedNeutralSample
                ? "using placeholder sample values; enter your own above, in \"Test this Expression\", to see this reflect them"
                : "using the sample values entered above";

    /// <summary>
    /// Joins a list of candidate values with the mockup's trailing ellipsis ("joe.bloggs1, joe.bloggs2,
    /// joe.bloggs3 …"). Empty when there are no candidates.
    /// </summary>
    public static string JoinCandidatesWithEllipsis(IReadOnlyList<string> candidates) =>
        candidates.Count == 0 ? string.Empty : string.Join(", ", candidates) + " …";

    /// <summary>
    /// The existing-object count line for an import-mode generated mapping on a saved Synchronisation Rule
    /// (plan Phase 3 point 2): "1,245 existing Metaverse Objects have no Account Name and would receive one on
    /// the next full synchronisation." Handles zero gracefully rather than reading as "nothing counted yet".
    /// </summary>
    public static string DescribeExistingObjectCount(int count, string attributeName)
    {
        if (count == 0)
            return $"Every existing Metaverse Object already has a value for {attributeName}.";

        var noun = count == 1 ? "Metaverse Object has" : "Metaverse Objects have";
        return $"{count:N0} existing {noun} no {attributeName} and would receive one on the next full synchronisation.";
    }

    /// <summary>
    /// Whether the existing-object count line applies at all (plan Phase 3 point 2: "hide it where it does not
    /// apply"): import mode only, and only once the Synchronisation Rule and the target attribute both exist.
    /// </summary>
    public static bool ShowExistingObjectCount(SyncRuleDirection direction, int syncRuleId, int? targetMetaverseAttributeId) =>
        direction == SyncRuleDirection.Import && syncRuleId > 0 && targetMetaverseAttributeId is > 0;

    /// <summary>
    /// The "Assigned so far" line under a Sequence mapping's Sequence state panel (approved mockup screen 02):
    /// "Assigned so far: N · belongs to Employee Number, not this flow · never below any flow's Start at".
    /// </summary>
    public static string DescribeAssignedSoFar(long assignedCount, string attributeName) =>
        $"Assigned so far: {assignedCount:N0} · belongs to {attributeName}, not this flow · never below any flow's Start at";

    /// <summary>
    /// The statement shown instead of a "next number" for a Sequence mapping whose counter has never been
    /// seeded: what the first number generated will be, per the mockup's own seeding rule.
    /// </summary>
    public static string DescribeUnseededCounter(string nextNumberFormatted) =>
        $"This counter has not issued a number yet. The first number generated will be {nextNumberFormatted}.";

    /// <summary>
    /// Whether the Add/Edit dialog's Save should show the mockup's "Skip ahead to N?" confirmation (plan Phase 3
    /// point 4, mockup screen 02): only for a Sequence mapping whose counter has already been seeded, and whose
    /// configured Start at now stands above the counter's next number. An unseeded counter has nothing to skip
    /// ahead from (<see cref="Application.UniqueValues.UniqueValueGenerationServer.RaiseSequenceStartIfHigherAsync"/>
    /// is a no-op in that case too), and a Start at at or below the counter moves nothing.
    /// </summary>
    public static bool ShouldConfirmSkipAhead(GeneratedValueSequenceState? currentState, long configuredSequenceStart) =>
        currentState is { IsSeeded: true } state && configuredSequenceStart > state.NextNumber;

    /// <summary>
    /// Whether a mapping's row should offer the "Start again…" action (plan Phase 3 point 5, mockup screen 02b):
    /// a saved (persisted) generated Sequence mapping only. It is offered for no other token kind in this
    /// release, since resetting a counter that does not exist for Only-if-taken or Random tokens does nothing.
    /// </summary>
    public static bool ShowStartAgainAction(SyncRuleMapping mapping) =>
        mapping.Id > 0 && mapping.Generation is { TokenKind: GeneratedValueTokenKind.Sequence };

    /// <summary>
    /// The result snackbar text after "Start again" completes (plan Phase 3 point 5), matching the wording
    /// <see cref="Application.Servers.ConnectedSystemServer"/> records on the Activity.
    /// </summary>
    public static string DescribeRestartResult(string attributeName, GeneratedValueRestartResult result) =>
        result.CounterFrom.HasValue
            ? $"Started {attributeName} again: the counter moved from {result.CounterFrom:N0} to {result.CounterTo:N0}."
            : $"\"Start again\" was requested for {attributeName}, but its counter had not issued any numbers yet, so nothing moved.";
}
