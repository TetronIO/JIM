// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Sync;
using JIM.Models.Staging;

namespace JIM.Application.UniqueValues;

/// <summary>
/// The forms of one object's generated value, worked out by replaying the derived pass on a scratch copy of the object
/// for each candidate (<see cref="GeneratedValueFormReplay"/>), and checked where each form is written
/// (<see cref="GeneratedValueForm"/>). Built by whatever resolves the request (synchronisation, Sync Preview, Collision
/// Remediation) from the object being synchronised, and answers each candidate once.
/// </summary>
public sealed class GeneratedValueFormSource : IGeneratedValueFormSource
{
    private readonly GeneratedValueFormReplay _replay;
    private readonly Dictionary<GeneratedValueBuiltValue, GeneratedValueForm> _forms;
    private readonly Dictionary<(string Candidate, long? Numeric), GeneratedValueFormEvaluation> _evaluated = [];

    private GeneratedValueFormSource(GeneratedValueFormReplay replay, IReadOnlyList<GeneratedValueForm> forms)
    {
        _replay = replay;
        _forms = forms.ToDictionary(f => f.BuiltValue);
    }

    /// <summary>
    /// The form source for <paramref name="pending"/>, the generation request <paramref name="cso"/>'s synchronisation
    /// recorded; null when it has no <paramref name="forms"/>, so a request without them is checked exactly as before.
    /// </summary>
    /// <param name="inScopeRules">The Synchronisation Rules in scope for the object being synchronised: the rules whose
    /// derived flows the synchronisation runs.</param>
    public static GeneratedValueFormSource? Create(
        ISyncEngine engine,
        ConnectedSystemObject cso,
        PendingGeneratedValue pending,
        IReadOnlyList<GeneratedValueForm> forms,
        IReadOnlyList<SyncRule> inScopeRules,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator expressionEvaluator,
        AttributePriorityContext priorityContext)
    {
        ArgumentNullException.ThrowIfNull(forms);
        if (forms.Count == 0)
            return null;

        var replay = new GeneratedValueFormReplay(engine, cso, pending, [.. forms.Select(f => f.BuiltValue)], inScopeRules, objectTypes, expressionEvaluator, priorityContext);
        return new GeneratedValueFormSource(replay, forms);
    }

    /// <inheritdoc />
    public GeneratedValueFormEvaluation Evaluate(string candidate, long? numericCandidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (_evaluated.TryGetValue((candidate, numericCandidate), out var known))
            return known;

        // Once per candidate per object: the added cost of checking the values built from a generated value.
        GeneratedValueFormReplayResult replayed;
        using (Diagnostics.Diagnostics.Sync.StartSpan("EvaluateGeneratedValueForms"))
            replayed = _replay.Replay(candidate, numericCandidate);
        var evaluation = replayed.IsWaiting
            ? GeneratedValueFormEvaluation.Waiting(replayed.Waits)
            : GeneratedValueFormEvaluation.Checking([.. replayed.Values.Select(v => CheckOf(_forms[v.Form], v.Value))]);

        _evaluated[(candidate, numericCandidate)] = evaluation;
        return evaluation;
    }

    private static GeneratedValueFormCheck CheckOf(GeneratedValueForm form, string value)
    {
        var builtValue = form.BuiltValue;
        var heldInMetaverse = builtValue.MetaverseAttributeId.HasValue;

        return new GeneratedValueFormCheck
        {
            Name = builtValue.MetaverseAttributeName ?? builtValue.Mapping.TargetConnectedSystemAttribute?.Name ?? "an exported value",
            Value = value,
            Mode = heldInMetaverse ? GeneratedValueMode.Import : GeneratedValueMode.Export,
            AttributeId = heldInMetaverse ? builtValue.MetaverseAttributeId!.Value : builtValue.Mapping.TargetConnectedSystemAttributeId!.Value,
            ConnectorSpaceAttributeIds = form.ConnectorSpaceAttributeIds,
            ProbeTargets = form.ProbeTargets,
            ProbeExemptValues = form.ProbeExemptValues,
            RejectedValues = form.RejectedValues
        };
    }
}
