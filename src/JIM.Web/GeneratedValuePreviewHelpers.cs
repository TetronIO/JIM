// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using JIM.Web.Localisation;
using JIM.Web.Models;
using JIM.Web.Resources;

namespace JIM.Web;

/// <summary>
/// Pure display and decision logic for the "Generated Value" Attribute Flow form's live preview panel, the
/// sequence state panel, the skip-ahead confirmation and the "Start again" row action (Unique Value Generation,
/// #242, Phase 3, Work Package D2). Kept free of Blazor/MudBlazor, <c>IExpressionEvaluator</c> and
/// <c>JimApplication</c> so it is directly unit-testable; see <c>GeneratedValuePreviewHelpersTests</c>. The
/// component wiring (evaluating the base expression, calling <c>DescribeGeneratedCandidates</c>) lives in
/// <c>GeneratedValuePreviewPanel.razor</c>. Written in the viewer's language (#3030): the words come from
/// <c>GeneratedValueText</c>.
/// </summary>
public static class GeneratedValuePreviewHelpers
{
    /// <summary>The mockup's static explanation for a Sequence token's "If a number is taken" line.</summary>
    public static string SequenceTakenExplanation => GeneratedValueText.PreviewSequenceTaken;

    /// <summary>The mockup's static "Source of randomness" line for a Random token's example.</summary>
    public static string RandomSourceOfRandomness => GeneratedValueText.PreviewRandomCryptographic;

    /// <summary>
    /// Whether the administrator has typed anything into any of "Test this Expression"'s sample inputs.
    /// </summary>
    public static bool HasAnySampleValue(IReadOnlyDictionary<string, string?> testerSampleValues) =>
        testerSampleValues.Values.Any(v => !string.IsNullOrEmpty(v));

    /// <summary>
    /// Builds the Metaverse and Connected System attribute dictionaries an <c>IExpressionEvaluator</c> needs to
    /// evaluate the base expression for the live preview: the administrator's own sample values from "Test this
    /// Expression" when any have been entered (missing inputs read as no value, exactly as the tester itself
    /// evaluates), otherwise every input filled with its own attribute name (<c>firstName</c> becomes the sample
    /// value <c>"firstName"</c>, which the expression's own <c>Lower()</c> then renders "firstname" exactly as a
    /// real value would be) so an expression with no entered samples still previews something meaningful, rather
    /// than a placeholder so generic that two different inputs render identically (e.g. "sample.sample").
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
                // The input is, by construction, always a simple mv[...]/cs[...] accessor (that is what
                // ExpressionInputResolver extracts), so its own AttributeName already IS "its name" for a
                // fallback; there is no case here that needs a further fallback.
                value = input.AttributeName;
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
                ? GeneratedValueText.PreviewSampleExample
                : GeneratedValueText.PreviewSampleEntered;

    /// <summary>
    /// The preview's caption when its candidates come from the base the tester's values give generation (#2471).
    /// </summary>
    public static string TriedValuesCaption => GeneratedValueText.PreviewTriedValues;

    /// <summary>
    /// Why the values tried give generation nothing (#2471): the base waits for an input, or could not be worked out.
    /// </summary>
    public static string DescribeNothingGenerated(GeneratedBaseOutcome outcome, string? targetName) => outcome == GeneratedBaseOutcome.Waits
        ? Format(GeneratedValueText.PreviewNothingWaits, targetName ?? GeneratedValueText.ImportTargetFallback)
        : Format(GeneratedValueText.PreviewNothingFailed, targetName ?? GeneratedValueText.ImportTargetFallback);

    /// <summary>
    /// Joins a list of candidate values with the mockup's trailing ellipsis ("joe.bloggs1, joe.bloggs2,
    /// joe.bloggs3 …"). Empty when there are no candidates.
    /// </summary>
    public static string JoinCandidatesWithEllipsis(IReadOnlyList<string> candidates) =>
        candidates.Count == 0 ? string.Empty : string.Join(", ", candidates) + " …";

    /// <summary>
    /// The existing-object count line for an import-mode generated mapping on a saved Synchronisation Rule
    /// (plan Phase 3 point 2): "1,245 existing Metaverse Objects have no Account Name and would receive one at
    /// the next synchronisation." Handles zero gracefully rather than reading as "nothing counted yet".
    /// </summary>
    public static string DescribeExistingObjectCount(int count, string attributeName)
    {
        if (count == 0)
            return Format(GeneratedValueText.PreviewExistingAllHaveValue, attributeName);

        var formatted = count.ToString("N0", CultureInfo.CurrentCulture);
        return Format(
            PluralRules.Choose(count, GeneratedValueText.PreviewExistingNoValueOne, GeneratedValueText.PreviewExistingNoValueOther),
            formatted, attributeName);
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
        Format(GeneratedValueText.PreviewAssignedSoFar, assignedCount.ToString("N0", CultureInfo.CurrentCulture), attributeName);

    /// <summary>
    /// The statement shown instead of a "next number" for a Sequence mapping whose counter has never been
    /// seeded: what the first number generated will be, per the mockup's own seeding rule.
    /// </summary>
    public static string DescribeUnseededCounter(string nextNumberFormatted) =>
        Format(GeneratedValueText.PreviewUnseeded, nextNumberFormatted);

    /// <summary>
    /// Formats a sequence counter value for display as an identifier, not a quantity (QA fix, #242 Phase 3 D2):
    /// no thousands separator, since "200000" and "100456" name a specific number the counter holds or will
    /// hold, they do not count anything. Zero-padded to the mapping's configured fixed width when one is set,
    /// mirroring the padding <c>UniqueValueCandidates.RenderSequenceNumber</c> (JIM.Application, internal, so
    /// not callable from here) applies to a real generated value, so a dialog or snackbar showing this counter
    /// reads identically to the live preview's own padded candidates. A value that no longer fits the width is
    /// shown unpadded rather than truncated; the sequence state panel's own width-exceeded warning is what
    /// flags that condition, not this formatter.
    /// </summary>
    public static string FormatSequenceIdentifier(long number, int? fixedWidth)
    {
        var raw = number.ToString(CultureInfo.InvariantCulture);
        return fixedWidth.HasValue && raw.Length <= fixedWidth.Value
            ? raw.PadLeft(fixedWidth.Value, '0')
            : raw;
    }

    /// <summary>
    /// Whether "Start again" would be a no-op (QA fix, #242 Phase 3 D2): the counter's current next number
    /// already equals the flow's configured Start at, so restarting would move it nowhere. The counter can
    /// never stand *behind* Start at when read via <c>GetGeneratedValueSequenceStateAsync</c>, whose
    /// <see cref="GeneratedValueSequenceState.NextNumber"/> is always the higher of the counter's own position
    /// and Start at (see that type's doc comment), so equality is the only "nothing to do" case reachable here;
    /// there is no separate "behind" branch to handle. Forgetting retired values (#242, Phase 6) is a change in its
    /// own right, so with any to forget the restart is never a no-op, even with the counter already at Start at.
    /// </summary>
    public static bool IsStartAgainNoOp(long currentNext, long configuredStart, int retiredValueCount) =>
        currentNext == configuredStart && retiredValueCount == 0;

    /// <summary>
    /// The "Start again" confirmation's count of what it forgets (#242, Phase 6, mockup section C): "4 retired
    /// values" or "1 retired value".
    /// </summary>
    public static string DescribeRetiredValueCount(int count) =>
        PluralRules.Choose(count, Format(GeneratedValueText.PreviewRetiredOne, count.ToString("N0", CultureInfo.CurrentCulture)),
            Format(GeneratedValueText.PreviewRetiredOther, count.ToString("N0", CultureInfo.CurrentCulture)));

    /// <summary>
    /// The mockup's "nothing will change" message for <see cref="IsStartAgainNoOp"/>, shown in place of the
    /// usual "Counter returns to X from Y" line and paired with disabling the confirm action.
    /// </summary>
    public static string DescribeStartAgainNoOp(long configuredStart, int? fixedWidth) =>
        Format(GeneratedValueText.PreviewStartAgainNoOp, FormatSequenceIdentifier(configuredStart, fixedWidth));

    /// <summary>
    /// The live preview's terse "Where the number goes" row (QA fix, #242 Phase 3 D2): "at the end", "before the
    /// @ (the value is email-shaped)", or, with no base value at all, "no base value: the value is the &lt;noun&gt;
    /// on its own" ("token" for a Random token, since it is not itself a number). Distinct from
    /// <c>UniqueValueGenerationServer.DescribeGeneratedCandidates</c>'s own <c>PlacementText</c>, which is a full,
    /// capitalised sentence shared with the REST and PowerShell surfaces; this is the compact, lower-case-after-
    /// label wording the live preview's own labelled row uses, matching its "First value"/"If taken" siblings.
    /// </summary>
    public static string DescribeNumberPlacement(bool hasBaseValue, bool baseValueIsEmailShaped, GeneratedValueTokenKind tokenKind)
    {
        if (!hasBaseValue)
        {
            var noun = tokenKind == GeneratedValueTokenKind.Random
                ? GeneratedValueText.PreviewNounToken
                : GeneratedValueText.PreviewNounNumber;
            return Format(GeneratedValueText.PreviewNoBaseValue, noun);
        }

        return baseValueIsEmailShaped ? GeneratedValueText.PreviewPlacementBeforeAt : GeneratedValueText.PreviewPlacementAtEnd;
    }

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
    /// <see cref="Application.Servers.ConnectedSystemServer"/> records on the Activity. <paramref name="fixedWidth"/>
    /// is the mapping's configured fixed width (QA fix, #242 Phase 3 D2): the counter values are identifiers, not
    /// quantities, so they render zero-padded and without a thousands separator via <see cref="FormatSequenceIdentifier"/>.
    /// </summary>
    public static string DescribeRestartResult(string attributeName, GeneratedValueRestartResult result, int? fixedWidth)
    {
        var counter = result.CounterFrom.HasValue
            ? Format(GeneratedValueText.PreviewRestartStarted, attributeName,
                FormatSequenceIdentifier(result.CounterFrom.Value, fixedWidth),
                FormatSequenceIdentifier(result.CounterTo!.Value, fixedWidth))
            : Format(GeneratedValueText.PreviewRestartNotSeeded, attributeName);

        // #242, Phase 6: what was forgotten is part of the outcome, and the counter sentence alone would hide it.
        if (result.RetiredValuesForgotten <= 0)
            return counter;

        var forgotten = Format(
            PluralRules.Choose(result.RetiredValuesForgotten, GeneratedValueText.PreviewRestartForgottenOne, GeneratedValueText.PreviewRestartForgottenOther),
            result.RetiredValuesForgotten.ToString("N0", CultureInfo.CurrentCulture));
        return $"{counter} {forgotten}";
    }

    private static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);
}
