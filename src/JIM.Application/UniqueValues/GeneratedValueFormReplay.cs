// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;

namespace JIM.Application.UniqueValues;

/// <summary>
/// Works out a generated value candidate's forms (Generated Value Forms, #2023) by replaying the synchronisation's
/// derived pass on a scratch copy of the Metaverse Object: the candidate applied as the generated value
/// (<see cref="ISyncEngine.ApplyGeneratedValue"/>), then every derived flow the forms need, level by level, through the
/// very code the derived pass runs (<see cref="SyncEngine.EvaluateDerivedMapping"/>). Attribute Priority, "Null is a
/// value", case normalisation and provenance therefore behave exactly as they will once the value is issued: a form's
/// value here is the value the pass then writes. The real object is never touched.
/// <para>
/// The flows replayed are those the forms need and nothing else: each form's own, and every one producing an input of
/// another in the set, at levels above the generated value's own (anything at or below it is already settled when the
/// value is resolved). Like the pass, only flows on the Synchronisation Rules in scope for the object being synchronised
/// run; every other value is read as the object holds it.
/// </para>
/// <para>
/// A form applies unless a contributor with higher Attribute Priority holds its attribute, or any value on its way from
/// the generated value: then it does not move with the candidate, and is not checked (PRD requirement 2). A form whose
/// flow, or a flow on its way, reads another input the object has no value for makes the candidate wait, whatever that
/// flow's Missing Input Behaviour (requirement 3). A form whose own chain gives no value has nothing to check. An
/// expression that fails throws, as it would in the pass, so the candidate is never issued (requirement 6).
/// </para>
/// </summary>
internal sealed class GeneratedValueFormReplay
{
    private readonly ISyncEngine _engine;
    private readonly ConnectedSystemObject _cso;
    private readonly MetaverseObject _mvo;
    private readonly PendingGeneratedValue _pending;
    private readonly IReadOnlyList<GeneratedValueBuiltValue> _forms;
    private readonly IReadOnlyList<ConnectedSystemObjectType> _objectTypes;
    private readonly IExpressionEvaluator _expressionEvaluator;
    private readonly AttributePriorityContext _priorityContext;

    /// <summary>
    /// The derived flows the forms need, by level, in the order the pass evaluates them.
    /// </summary>
    private readonly SortedDictionary<int, List<(SyncRule Rule, SyncRuleMapping Mapping)>> _replayed = [];

    /// <summary>
    /// The Metaverse attributes whose value moves with the candidate: the generated attribute, and the target of every
    /// replayed flow reading one of them.
    /// </summary>
    private readonly HashSet<int> _builtFromCandidate = [];

    private readonly Dictionary<string, int> _attributeIdsByName = new(StringComparer.OrdinalIgnoreCase);

    public GeneratedValueFormReplay(
        ISyncEngine engine,
        ConnectedSystemObject cso,
        PendingGeneratedValue pending,
        IReadOnlyList<GeneratedValueBuiltValue> forms,
        IReadOnlyList<SyncRule> inScopeRules,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator expressionEvaluator,
        AttributePriorityContext priorityContext)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(cso);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(forms);
        ArgumentNullException.ThrowIfNull(inScopeRules);
        ArgumentNullException.ThrowIfNull(objectTypes);
        ArgumentNullException.ThrowIfNull(expressionEvaluator);
        ArgumentNullException.ThrowIfNull(priorityContext);

        _engine = engine;
        _cso = cso;
        _mvo = cso.MetaverseObject ?? throw new ArgumentException("The Connected System Object is not joined to a Metaverse Object.", nameof(cso));
        _pending = pending;
        _forms = forms;
        _objectTypes = objectTypes;
        _expressionEvaluator = expressionEvaluator;
        _priorityContext = priorityContext;

        // Names resolve from whatever describes the object's attributes: its type when loaded with them, its values, and
        // the flows that write them, so a load that leaves the type's attributes out cannot make a form's chain unreadable.
        var known = (_mvo.Type?.Attributes ?? [])
            .Concat(_mvo.AttributeValues.Select(av => av.Attribute))
            .Concat(inScopeRules.SelectMany(rule => rule.AttributeFlowRules).Select(m => m.TargetMetaverseAttribute))
            .Append(pending.Mapping.TargetMetaverseAttribute)
            .OfType<MetaverseAttribute>();
        foreach (var attribute in known)
            _attributeIdsByName.TryAdd(attribute.Name, attribute.Id);
        foreach (var form in forms.Where(f => f.MetaverseAttributeId.HasValue && f.MetaverseAttributeName != null))
            _attributeIdsByName.TryAdd(form.MetaverseAttributeName!, form.MetaverseAttributeId!.Value);

        PlanReplay(inScopeRules);
    }

    /// <summary>
    /// The forms of <paramref name="candidate"/> (with <paramref name="numericCandidate"/> for a Number target).
    /// </summary>
    /// <exception cref="SyncExpressionEvaluationException">A form's expression, or one on its way, failed.</exception>
    public GeneratedValueFormReplayResult Replay(string? candidate, long? numericCandidate)
    {
        var scratch = CopyOf(_mvo);
        var scratchCso = ShellOf(_cso, scratch);
        _engine.ApplyGeneratedValue(scratch, _pending, candidate, numericCandidate);

        var waits = new List<GeneratedValueFormWait>();
        var applies = new Dictionary<SyncRuleMapping, bool>(ReferenceEqualityComparer.Instance);

        // Mapping-level faults are the pass's to report once the value is issued; a form they leave with no value has
        // nothing written to a target, so nothing to check.
        var errors = new List<AttributeFlowError>();

        foreach (var hops in _replayed.Values)
        {
            // Built once per level, as the pass builds it.
            var metaverseAttributes = SyncEngine.BuildEffectiveAttributeDictionary(scratch);
            foreach (var (rule, mapping) in hops)
            {
                var target = mapping.TargetMetaverseAttribute!.Id;
                if (_builtFromCandidate.Contains(target))
                {
                    applies[mapping] = Applies(scratch, rule, mapping, target);

                    // A flow that does not apply contributes nothing whatever its inputs; one that does and lacks another
                    // input cannot be worked out, and is not run (it could fail the object for an input not yet arrived).
                    var missing = applies[mapping] ? OtherMissingInputs(scratchCso, mapping, metaverseAttributes) : [];
                    if (missing.Count > 0)
                    {
                        waits.AddRange(missing.Select(input => new GeneratedValueFormWait(input, mapping.TargetMetaverseAttribute.Name)));
                        continue;
                    }
                }

                SyncEngine.EvaluateDerivedMapping(scratchCso, rule, mapping, _objectTypes, _expressionEvaluator, _priorityContext, metaverseAttributes, errors);
            }
        }

        var values = new List<(GeneratedValueBuiltValue Form, string Value)>();
        var settled = SyncEngine.BuildEffectiveAttributeDictionary(scratch);
        foreach (var form in _forms.Where(f => ChainApplies(f, applies)))
        {
            var value = form.MetaverseAttributeId.HasValue
                ? ValueOf(scratch, form.MetaverseAttributeId.Value)
                : ExportExpressionValueOf(form, settled, waits);

            if (!string.IsNullOrEmpty(value))
                values.Add((form, value));
        }

        return waits.Count > 0
            ? GeneratedValueFormReplayResult.Waiting([.. waits.Distinct()])
            : GeneratedValueFormReplayResult.Worked(values);
    }

    /// <summary>
    /// Works out which derived flows the forms need, and which of their targets move with the candidate.
    /// </summary>
    private void PlanReplay(IReadOnlyList<SyncRule> inScopeRules)
    {
        var graph = _priorityContext.DerivedFlowGraph;
        if (graph == null)
        {
            _builtFromCandidate.Add(_pending.AttributeId);
            return;
        }

        var typeId = _mvo.Type?.Id ?? _pending.Mapping.SyncRule?.MetaverseObjectTypeId ?? 0;
        var generatedLevel = graph.GetLevel(typeId, _pending.AttributeId) ?? 0;

        // Every in-scope derived flow above the generated value's level, by the attribute it writes.
        var byLevel = new SortedDictionary<int, List<(SyncRule Rule, SyncRuleMapping Mapping)>>();
        var byTarget = new Dictionary<int, List<SyncRuleMapping>>();
        for (var level = generatedLevel + 1; level <= graph.MaxLevel(typeId); level++)
        {
            byLevel[level] = SyncEngine.FindDerivedMappingsInScope(graph, level, inScopeRules)
                .Where(hop => hop.SyncRule.MetaverseObjectTypeId == typeId)
                .ToList();

            foreach (var (_, mapping) in byLevel[level])
            {
                var target = mapping.TargetMetaverseAttribute!.Id;
                if (!byTarget.TryGetValue(target, out var writers))
                    byTarget[target] = writers = [];
                writers.Add(mapping);
            }
        }

        // Backwards from what the forms hold, or read, to everything that produces it.
        var needed = new Queue<int>(_forms.SelectMany(NeededAttributeIds));
        var seen = new HashSet<int>();
        var closure = new HashSet<SyncRuleMapping>(ReferenceEqualityComparer.Instance);
        while (needed.Count > 0)
        {
            var attributeId = needed.Dequeue();
            if (!seen.Add(attributeId) || !byTarget.TryGetValue(attributeId, out var writers))
                continue;

            foreach (var input in writers.Where(closure.Add).SelectMany(graph.GetMetaverseInputIds))
                needed.Enqueue(input);
        }

        // Forwards from the generated value, in the pass's order, to everything that moves with it.
        _builtFromCandidate.Add(_pending.AttributeId);
        foreach (var (level, hops) in byLevel)
        {
            var replayed = hops.Where(hop => closure.Contains(hop.Mapping)).ToList();
            if (replayed.Count == 0)
                continue;

            _replayed[level] = replayed;
            foreach (var (_, mapping) in replayed.Where(hop => graph.GetMetaverseInputIds(hop.Mapping).Any(_builtFromCandidate.Contains)))
                _builtFromCandidate.Add(mapping.TargetMetaverseAttribute!.Id);
        }
    }

    /// <summary>
    /// The Metaverse attributes a form's value is read from: its own, for a derived form; the expression's inputs, for an
    /// export expression.
    /// </summary>
    private IEnumerable<int> NeededAttributeIds(GeneratedValueBuiltValue form)
    {
        if (form.MetaverseAttributeId.HasValue)
            return [form.MetaverseAttributeId.Value];

        return DerivedFlowGraph.GetMetaverseInputNames(form.Mapping)
            .Where(_attributeIdsByName.ContainsKey)
            .Select(name => _attributeIdsByName[name]);
    }

    /// <summary>
    /// Whether every flow on a form's way from the generated value applied for this candidate: the form's own (for a
    /// derived form), and the one writing each attribute it is built through. Every one of them is on the generated
    /// flow's own Synchronisation Rule, which is what makes it a form; an export expression's own rule only writes it.
    /// </summary>
    private bool ChainApplies(GeneratedValueBuiltValue form, Dictionary<SyncRuleMapping, bool> applies)
    {
        var chain = form.Via.Select(name => _attributeIdsByName.TryGetValue(name, out var id) ? id : (int?)null).ToList();
        if (form.MetaverseAttributeId.HasValue)
            chain.Add(form.MetaverseAttributeId.Value);

        foreach (var attributeId in chain)
        {
            var hop = attributeId.HasValue ? HopWriting(attributeId.Value) : null;
            if (hop == null || !applies.TryGetValue(hop, out var applied) || !applied)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The replayed flow on the generated flow's own Synchronisation Rule that writes <paramref name="attributeId"/>.
    /// </summary>
    private SyncRuleMapping? HopWriting(int attributeId)
    {
        var ownRule = _pending.Mapping.SyncRule;
        var ownRuleId = _pending.Mapping.SyncRuleId;

        return _replayed.Values.SelectMany(hops => hops)
            .Where(hop => hop.Mapping.TargetMetaverseAttribute!.Id == attributeId
                && ((ownRule != null && ReferenceEquals(hop.Rule, ownRule)) || (ownRuleId > 0 && hop.Rule.Id == ownRuleId)))
            .Select(hop => hop.Mapping)
            .FirstOrDefault();
    }

    /// <summary>
    /// The Attribute Priority gate as <see cref="SyncEngine"/> applies it to a contribution, at the moment the pass would
    /// apply it: a contribution to an attribute with more than one contributor goes ahead only if it wins against the
    /// attribute's current owner.
    /// </summary>
    private bool Applies(MetaverseObject scratch, SyncRule rule, SyncRuleMapping mapping, int target) =>
        _priorityContext.GetContributorCount(rule.MetaverseObjectTypeId, target) <= 1
        || _priorityContext.ShouldApply(rule.MetaverseObjectTypeId, target, mapping, SyncEngine.FindEffectiveIncumbentSyncRuleId(scratch, target));

    /// <summary>
    /// The inputs a flow reads, other than values moving with the candidate, that the object has no value for, by name.
    /// </summary>
    private List<string> OtherMissingInputs(ConnectedSystemObject scratchCso, SyncRuleMapping mapping, IDictionary<string, object?> metaverseAttributes)
    {
        var csoType = _objectTypes.Single(t => t.Id == scratchCso.TypeId);
        var csAttributes = SyncEngine.BuildCsoAttributeDictionary(scratchCso, csoType);

        return mapping.Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Expression))
            .SelectMany(source => MissingInputs(source.Expression!, csAttributes, metaverseAttributes))
            .Distinct()
            .ToList();
    }

    private IEnumerable<string> MissingInputs(string expression, IDictionary<string, object?>? csAttributes, IDictionary<string, object?> metaverseAttributes)
    {
        var missing = ExpressionInputResolver.FindMissingInputs(expression, ExpressionInputSource.Metaverse, metaverseAttributes);
        if (csAttributes != null)
            missing = [.. missing, .. ExpressionInputResolver.FindMissingInputs(expression, ExpressionInputSource.ConnectedSystem, csAttributes)];

        // A value moving with the candidate that has none is the chain itself giving no value: nothing to check, not a
        // reason to wait.
        return ExpressionInputResolver.ResolveCached(expression)
            .Where(input => missing.Contains(input.Accessor)
                && !(input.Source == ExpressionInputSource.Metaverse
                    && _attributeIdsByName.TryGetValue(input.AttributeName, out var id)
                    && _builtFromCandidate.Contains(id)))
            .Select(input => input.AttributeName);
    }

    /// <summary>
    /// What an export expression form would send, from the scratch object's settled values; null when it sends nothing
    /// or its value is not text. Another input with no value is added to <paramref name="waits"/>.
    /// </summary>
    private string? ExportExpressionValueOf(GeneratedValueBuiltValue form, Dictionary<string, object?> settled, List<GeneratedValueFormWait> waits)
    {
        var source = form.Mapping.Sources.OrderBy(s => s.Order).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Expression));
        if (source == null)
            return null;

        var missing = MissingInputs(source.Expression!, csAttributes: null, settled).ToList();
        if (missing.Count > 0)
        {
            var name = form.Mapping.TargetConnectedSystemAttribute?.Name ?? "an exported value";
            waits.AddRange(missing.Select(input => new GeneratedValueFormWait(input, name)));
            return null;
        }

        try
        {
            return SyncEngine.EvaluateExportExpressionValue(form.Mapping, source, settled, _expressionEvaluator) as string;
        }
        catch (SyncExpressionEvaluationException ex)
        {
            ex.SyncRuleName ??= form.SyncRule.Name;
            throw;
        }
    }

    /// <summary>
    /// The scratch object's value for an attribute once its pending changes apply: the latest pending addition (none
    /// for an asserted null), otherwise the value it holds and is not removing.
    /// </summary>
    private static string? ValueOf(MetaverseObject scratch, int attributeId)
    {
        var added = scratch.PendingAttributeValueAdditions.LastOrDefault(av => av.AttributeId == attributeId);
        if (added != null)
            return added.NullValue ? null : added.StringValue;

        return SyncEngine.GetEffectiveAttributeValues(scratch, attributeId)
            .Where(av => !av.NullValue)
            .Select(av => av.StringValue)
            .FirstOrDefault();
    }

    /// <summary>
    /// A scratch copy of <paramref name="mvo"/> as the pass sees it once its generation requests are settled: its values,
    /// pending changes and all, copied (Attribute Flow stamps provenance on a value in place), and no pending requests.
    /// </summary>
    private static MetaverseObject CopyOf(MetaverseObject mvo)
    {
        var copy = new MetaverseObject
        {
            Id = mvo.Id,
            Type = mvo.Type,
            Origin = mvo.Origin,
            CachedDisplayName = mvo.CachedDisplayName
        };

        var copies = new Dictionary<MetaverseObjectAttributeValue, MetaverseObjectAttributeValue>(ReferenceEqualityComparer.Instance);
        foreach (var value in mvo.AttributeValues)
            copy.AttributeValues.Add(copies[value] = value.CopyFor(copy));
        foreach (var value in mvo.PendingAttributeValueAdditions)
            copy.PendingAttributeValueAdditions.Add(copies[value] = value.CopyFor(copy));
        foreach (var value in mvo.PendingAttributeValueRemovals)
            copy.PendingAttributeValueRemovals.Add(copies.TryGetValue(value, out var copied) ? copied : value);

        return copy;
    }

    /// <summary>
    /// The Connected System Object as Attribute Flow reads it, joined to <paramref name="scratch"/>. Attribute Flow never
    /// writes to a Connected System Object, so its values are shared, not copied.
    /// </summary>
    private static ConnectedSystemObject ShellOf(ConnectedSystemObject cso, MetaverseObject scratch) => new()
    {
        Id = cso.Id,
        TypeId = cso.TypeId,
        Type = cso.Type,
        ConnectedSystemId = cso.ConnectedSystemId,
        AttributeValues = cso.AttributeValues,
        MetaverseObject = scratch,
        MetaverseObjectId = cso.MetaverseObjectId
    };
}
