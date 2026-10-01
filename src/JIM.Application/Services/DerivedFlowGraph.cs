// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// The dependency graph of Metaverse-Derived Attribute Flows (#1750), keyed per Metaverse Object Type. Pure: it
/// performs no I/O, and is built from Synchronisation Rules and Metaverse Object Types the caller already holds.
/// </summary>
/// <remarks>
/// <para>
/// A mapping is <em>derived</em> when it is an import mapping (an expression mapping, or a generated mapping through
/// its base expression, #242) whose expression reads at least one <c>mv["..."]</c> attribute, detected with
/// <see cref="ExpressionInputResolver"/> rather than declared (plan decision 1). Nodes are the Metaverse attributes of
/// a type; each resolved <c>mv</c> input of a derived mapping is an edge from that input to the mapping's target.
/// Names resolve case-insensitively; a name that is not an attribute of the type is recorded in
/// <see cref="UnknownInputs"/> and contributes no edge.
/// </para>
/// <para>
/// Levels come from Kahn's algorithm (plan decision 3): an attribute with no derived contributor is level 0;
/// otherwise its level is one more than the deepest input of any of its derived contributors, and every derived
/// contributor of an attribute is evaluated at that attribute's level (decision 4), ordered canonically by
/// Attribute Priority's own (priority, mapping id) order. Attributes left over by Kahn's algorithm cannot be
/// ordered; the strongly connected sets among them are reported in <see cref="Cycles"/>, a self-reference being a
/// cycle of one.
/// </para>
/// <para>
/// <see cref="DerivedFlowGraphScope.AllMappings"/> (save-time validation) includes disabled mappings and mappings of
/// disabled rules, so enabling a flow can never introduce a cycle; <see cref="DerivedFlowGraphScope.EnabledMappingsOnly"/>
/// is what synchronisation evaluates (decision 2).
/// </para>
/// </remarks>
public sealed class DerivedFlowGraph
{
    private readonly Dictionary<SyncRuleMapping, DerivedFlow> _flowsByMapping = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, DerivedFlow> _flowsByPersistedMappingId = new();
    private readonly Dictionary<int, TypeGraph> _typeGraphs = new();
    private readonly List<DerivedFlowCycle> _cycles = [];
    private readonly List<DerivedFlowUnknownInput> _unknownInputs = [];

    /// <summary>
    /// Builds the graph.
    /// </summary>
    /// <param name="syncRules">Synchronisation Rules of any direction and any Connected System; only import rules are
    /// considered.</param>
    /// <param name="metaverseObjectTypes">The Metaverse Object Types the rules flow to, with their attributes loaded;
    /// <c>mv</c> names are resolved against these. A rule whose type is absent here falls back to its own
    /// <see cref="SyncRule.MetaverseObjectType"/> navigation, if loaded.</param>
    /// <param name="scope">Whether disabled mappings and rules are included.</param>
    public DerivedFlowGraph(IEnumerable<SyncRule> syncRules, IEnumerable<MetaverseObjectType> metaverseObjectTypes, DerivedFlowGraphScope scope)
    {
        ArgumentNullException.ThrowIfNull(syncRules);
        ArgumentNullException.ThrowIfNull(metaverseObjectTypes);

        var suppliedTypes = new Dictionary<int, MetaverseObjectType>();
        foreach (var type in metaverseObjectTypes)
            suppliedTypes.TryAdd(type.Id, type);

        var includeDisabled = scope == DerivedFlowGraphScope.AllMappings;
        var importRules = syncRules
            .Where(rule => rule.Direction == SyncRuleDirection.Import && (includeDisabled || rule.Enabled))
            .ToList();

        foreach (var rule in importRules)
        {
            var typeGraph = GetOrCreateTypeGraph(rule, suppliedTypes);
            var candidateMappings = rule.AttributeFlowRules
                .Where(mapping => GetTargetAttributeId(mapping) != null && (includeDisabled || mapping.Enabled));

            foreach (var mapping in candidateMappings)
                AddMappingIfDerived(typeGraph, rule, mapping);
        }

        foreach (var typeGraph in _typeGraphs.Values.OrderBy(t => t.MetaverseObjectTypeId))
            ComputeLevelsAndCycles(typeGraph);
    }

    /// <summary>
    /// Every dependency cycle found, across all Metaverse Object Types.
    /// </summary>
    public IReadOnlyList<DerivedFlowCycle> Cycles => _cycles;

    /// <summary>
    /// Whether any Metaverse Object Type has a dependency cycle.
    /// </summary>
    public bool HasCycle => _cycles.Count > 0;

    /// <summary>
    /// Every <c>mv["..."]</c> name, read by a derived mapping, that is not an attribute of the rule's Metaverse Object Type.
    /// </summary>
    public IReadOnlyList<DerivedFlowUnknownInput> UnknownInputs => _unknownInputs;

    /// <summary>
    /// Whether <paramref name="mapping"/> is a derived import mapping in this graph. Matched by reference first (a
    /// proposal may carry unsaved mappings), then by persisted mapping id.
    /// </summary>
    public bool IsDerived(SyncRuleMapping mapping) => GetDerivedFlow(mapping) != null;

    /// <summary>
    /// The resolved derived flow for <paramref name="mapping"/>, or null when it is not derived in this graph.
    /// </summary>
    public DerivedFlow? GetDerivedFlow(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (_flowsByMapping.TryGetValue(mapping, out var flow))
            return flow;

        return mapping.Id > 0 && _flowsByPersistedMappingId.TryGetValue(mapping.Id, out var byId) ? byId : null;
    }

    /// <summary>
    /// The Metaverse attribute ids <paramref name="mapping"/>'s expression reads with <c>mv["..."]</c>, in the order
    /// it first mentions them; empty when the mapping is not derived. Unknown names are excluded.
    /// </summary>
    public IReadOnlyList<int> GetMetaverseInputIds(SyncRuleMapping mapping) =>
        GetDerivedFlow(mapping)?.Inputs.Select(input => input.Id).ToList() ?? [];

    /// <summary>
    /// The level <paramref name="metaverseAttributeId"/> is evaluated at for the given Metaverse Object Type: 0 when
    /// no derived flow contributes to it (including an attribute this graph knows nothing about), otherwise one
    /// more than its deepest derived input. Null when the attribute cannot be ordered because it is on, or depends
    /// on, a dependency cycle.
    /// </summary>
    public int? GetLevel(int metaverseObjectTypeId, int metaverseAttributeId)
    {
        if (!_typeGraphs.TryGetValue(metaverseObjectTypeId, out var typeGraph))
            return 0;

        if (typeGraph.Unordered.Contains(metaverseAttributeId))
            return null;

        return typeGraph.Levels.TryGetValue(metaverseAttributeId, out var level) ? level : 0;
    }

    /// <summary>
    /// The deepest level of the given Metaverse Object Type; 0 when it has no derived flows.
    /// </summary>
    public int MaxLevel(int metaverseObjectTypeId) =>
        _typeGraphs.TryGetValue(metaverseObjectTypeId, out var typeGraph) ? typeGraph.MaxLevel : 0;

    /// <summary>
    /// The derived mappings evaluated at <paramref name="level"/> for the given Metaverse Object Type, in canonical
    /// order: Attribute Priority's (priority, mapping id), then Synchronisation Rule id and target attribute id so
    /// that even unsaved mappings order deterministically. Level 0 is always empty.
    /// </summary>
    public IReadOnlyList<SyncRuleMapping> GetDerivedMappings(int metaverseObjectTypeId, int level)
    {
        if (!_typeGraphs.TryGetValue(metaverseObjectTypeId, out var typeGraph) || !typeGraph.FlowsByLevel.TryGetValue(level, out var flows))
            return [];

        return flows.Select(flow => flow.Mapping).ToList();
    }

    /// <summary>
    /// The Connected System ids whose import Synchronisation Rules host a derived flow reading
    /// <paramref name="metaverseAttributeId"/>, directly or transitively through derived levels (if Email reads
    /// Account Name and User Principal Name reads Email, a change to Account Name reaches the hosts of both). Ordered
    /// ascending; empty when nothing derived reads the attribute.
    /// </summary>
    public IReadOnlyList<int> GetHostingSystemsReading(int metaverseObjectTypeId, int metaverseAttributeId)
    {
        if (!_typeGraphs.TryGetValue(metaverseObjectTypeId, out var typeGraph))
            return [];

        var hostingSystems = new SortedSet<int>();
        var visited = new HashSet<int> { metaverseAttributeId };
        var frontier = new Queue<int>();
        frontier.Enqueue(metaverseAttributeId);

        while (frontier.TryDequeue(out var attributeId))
        {
            if (!typeGraph.ReadersByInput.TryGetValue(attributeId, out var readers))
                continue;

            foreach (var reader in readers)
            {
                hostingSystems.Add(reader.SyncRule.ConnectedSystemId);
                if (visited.Add(reader.TargetAttributeId))
                    frontier.Enqueue(reader.TargetAttributeId);
            }
        }

        return hostingSystems.ToList();
    }

    /// <summary>
    /// The name of a Metaverse Object Type this graph was built over, for messages.
    /// </summary>
    public string GetMetaverseObjectTypeName(int metaverseObjectTypeId) =>
        _typeGraphs.TryGetValue(metaverseObjectTypeId, out var typeGraph) ? typeGraph.Name : $"ID {metaverseObjectTypeId}";

    /// <summary>
    /// The Metaverse attribute names an import mapping's expression sources read with <c>mv["..."]</c>, distinct
    /// case-insensitively, in the order they are first mentioned. A generated mapping's base expression is its
    /// <c>Sources[0].Expression</c>, so it is covered by the same read.
    /// </summary>
    public static IReadOnlyList<string> GetMetaverseInputNames(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return mapping.Sources
            .OrderBy(source => source.Order)
            .Where(source => !string.IsNullOrWhiteSpace(source.Expression))
            .SelectMany(source => ExpressionInputResolver.ResolveCached(source.Expression))
            .Where(input => input.Source == ExpressionInputSource.Metaverse)
            .Select(input => input.AttributeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether an import mapping's expression reads any <c>mv["..."]</c> attribute, i.e. whether it would be derived.
    /// </summary>
    public static bool ReadsMetaverse(SyncRuleMapping mapping) => GetMetaverseInputNames(mapping).Count > 0;

    private static int? GetTargetAttributeId(SyncRuleMapping mapping) => mapping.ResolveTargetMetaverseAttributeId();

    private TypeGraph GetOrCreateTypeGraph(SyncRule rule, Dictionary<int, MetaverseObjectType> suppliedTypes)
    {
        var typeId = rule.ResolveMetaverseObjectTypeId();
        if (_typeGraphs.TryGetValue(typeId, out var existing))
            return existing;

        var type = suppliedTypes.GetValueOrDefault(typeId) ?? rule.MetaverseObjectType;
        var typeGraph = new TypeGraph(typeId, type?.Name ?? $"ID {typeId}");
        foreach (var attribute in type?.Attributes ?? [])
        {
            typeGraph.AttributesByName.TryAdd(attribute.Name, attribute);
            typeGraph.AttributesById.TryAdd(attribute.Id, attribute);
        }

        _typeGraphs[typeId] = typeGraph;
        return typeGraph;
    }

    private void AddMappingIfDerived(TypeGraph typeGraph, SyncRule rule, SyncRuleMapping mapping)
    {
        var names = GetMetaverseInputNames(mapping);
        if (names.Count == 0)
            return;

        _unknownInputs.AddRange(names
            .Where(name => !typeGraph.AttributesByName.ContainsKey(name))
            .Select(name => new DerivedFlowUnknownInput(mapping, rule, typeGraph.MetaverseObjectTypeId, typeGraph.Name, name)));

        var inputs = names
            .Where(typeGraph.AttributesByName.ContainsKey)
            .Select(name => typeGraph.AttributesByName[name])
            .DistinctBy(attribute => attribute.Id)
            .ToList();

        var targetId = GetTargetAttributeId(mapping)!.Value;
        var target = typeGraph.AttributesById.GetValueOrDefault(targetId) ?? mapping.TargetMetaverseAttribute;
        var flow = new DerivedFlow(
            mapping,
            rule,
            typeGraph.MetaverseObjectTypeId,
            targetId,
            target?.Name ?? $"ID {targetId}",
            target?.Type ?? AttributeDataType.NotSet,
            inputs);

        typeGraph.Flows.Add(flow);
        _flowsByMapping[mapping] = flow;
        if (mapping.Id > 0)
            _flowsByPersistedMappingId.TryAdd(mapping.Id, flow);
    }

    private void ComputeLevelsAndCycles(TypeGraph typeGraph)
    {
        typeGraph.Flows.Sort(CompareCanonically);

        foreach (var flow in typeGraph.Flows)
        {
            typeGraph.AttributeNames.TryAdd(flow.TargetAttributeId, flow.TargetAttributeName);
            AddToList(typeGraph.ContributorsByTarget, flow.TargetAttributeId, flow);
            foreach (var input in flow.Inputs)
            {
                typeGraph.AttributeNames.TryAdd(input.Id, input.Name);
                AddToList(typeGraph.ReadersByInput, input.Id, flow);
            }
        }

        // Kahn's algorithm over the attributes that take part in any edge. An attribute outside every edge is level
        // 0 by definition and needs no entry.
        var nodes = new HashSet<int>(typeGraph.ContributorsByTarget.Keys);
        nodes.UnionWith(typeGraph.ReadersByInput.Keys);

        var inDegree = nodes.ToDictionary(node => node, _ => 0);
        foreach (var flow in typeGraph.Flows)
            inDegree[flow.TargetAttributeId] += flow.Inputs.Count;

        var levels = nodes.ToDictionary(node => node, node => typeGraph.ContributorsByTarget.ContainsKey(node) ? 1 : 0);
        var ready = new Queue<int>(nodes.Where(node => inDegree[node] == 0).OrderBy(node => node));
        var ordered = new HashSet<int>();

        while (ready.TryDequeue(out var node))
        {
            ordered.Add(node);
            if (!typeGraph.ReadersByInput.TryGetValue(node, out var readers))
                continue;

            foreach (var target in readers.Select(reader => reader.TargetAttributeId))
            {
                levels[target] = Math.Max(levels[target], levels[node] + 1);
                inDegree[target]--;
                if (inDegree[target] == 0)
                    ready.Enqueue(target);
            }
        }

        foreach (var node in ordered)
            typeGraph.Levels[node] = levels[node];

        typeGraph.Unordered.UnionWith(nodes.Where(node => !ordered.Contains(node)));

        foreach (var flow in typeGraph.Flows.Where(flow => ordered.Contains(flow.TargetAttributeId)))
            AddToList(typeGraph.FlowsByLevel, typeGraph.Levels[flow.TargetAttributeId], flow);

        typeGraph.MaxLevel = typeGraph.Levels.Count == 0 ? 0 : typeGraph.Levels.Values.Max();

        if (typeGraph.Unordered.Count > 0)
            ReportCycles(typeGraph);
    }

    /// <summary>
    /// Finds the strongly connected sets among the attributes Kahn's algorithm could not order (Tarjan's algorithm,
    /// over "reads" edges from a derived attribute to its inputs), and reports each set that is genuinely cyclic: more
    /// than one attribute, or one attribute reading itself. Attributes merely downstream of a cycle are unordered too,
    /// but form no cycle of their own and are not reported.
    /// </summary>
    private void ReportCycles(TypeGraph typeGraph)
    {
        var components = new TarjanComponents(typeGraph).Find();

        foreach (var component in components.Where(component => IsCyclic(typeGraph, component)))
        {
            var path = FindCyclePath(typeGraph, component);
            var pathEdges = new HashSet<(SyncRuleMapping, int)>(path.Select(member => (member.Mapping, member.ReadsMetaverseAttributeId)));
            var additional = component
                .OrderBy(node => node)
                .SelectMany(node => ReadsEdges(typeGraph, node, component))
                .Where(edge => !pathEdges.Contains((edge.Flow.Mapping, edge.Input)))
                .Select(edge => ToMember(typeGraph, edge.Flow, edge.Input))
                .ToList();

            _cycles.Add(new DerivedFlowCycle(typeGraph.MetaverseObjectTypeId, path, additional));
        }
    }

    /// <summary>
    /// Whether a strongly connected set is a genuine cycle: more than one attribute, or one attribute reading itself.
    /// </summary>
    private static bool IsCyclic(TypeGraph typeGraph, HashSet<int> component)
    {
        if (component.Count > 1)
            return true;

        var only = component.First();
        return ReadsEdges(typeGraph, only, component).Any(edge => edge.Input == only);
    }

    /// <summary>
    /// A shortest closed path through <paramref name="component"/>, starting from its lowest attribute id and following
    /// "reads" edges (breadth-first, in canonical flow order) until it reads back to the start.
    /// </summary>
    private static List<DerivedFlowCycleMember> FindCyclePath(TypeGraph typeGraph, HashSet<int> component)
    {
        var start = component.Min();
        var cameFrom = new Dictionary<int, (int From, DerivedFlow Flow)>();
        var frontier = new Queue<int>();
        frontier.Enqueue(start);

        while (frontier.TryDequeue(out var node))
        {
            foreach (var edge in ReadsEdges(typeGraph, node, component))
            {
                if (edge.Input == start)
                    return BuildPath(typeGraph, start, node, edge.Flow, cameFrom);

                if (cameFrom.TryAdd(edge.Input, (node, edge.Flow)))
                    frontier.Enqueue(edge.Input);
            }
        }

        // Unreachable for a strongly connected set; fail loudly rather than report a partial cycle.
        throw new InvalidOperationException($"No closed dependency path was found through attribute {start}; the derived flow graph is inconsistent.");
    }

    private static List<DerivedFlowCycleMember> BuildPath(TypeGraph typeGraph, int start, int last, DerivedFlow closingFlow, Dictionary<int, (int From, DerivedFlow Flow)> cameFrom)
    {
        var reversed = new List<DerivedFlowCycleMember> { ToMember(typeGraph, closingFlow, start) };
        var node = last;
        while (node != start)
        {
            var (from, flow) = cameFrom[node];
            reversed.Add(ToMember(typeGraph, flow, node));
            node = from;
        }

        reversed.Reverse();
        return reversed;
    }

    private static IEnumerable<(DerivedFlow Flow, int Input)> ReadsEdges(TypeGraph typeGraph, int attributeId, HashSet<int> within)
    {
        if (!typeGraph.ContributorsByTarget.TryGetValue(attributeId, out var contributors))
            return [];

        return contributors
            .SelectMany(flow => flow.Inputs.Select(input => (Flow: flow, Input: input.Id)))
            .Where(edge => within.Contains(edge.Input));
    }

    private static DerivedFlowCycleMember ToMember(TypeGraph typeGraph, DerivedFlow flow, int readsAttributeId) => new(
        flow.Mapping,
        flow.SyncRule,
        flow.TargetAttributeId,
        flow.TargetAttributeName,
        readsAttributeId,
        typeGraph.AttributeNames.GetValueOrDefault(readsAttributeId) ?? $"ID {readsAttributeId}");

    private static int CompareCanonically(DerivedFlow a, DerivedFlow b)
    {
        var byPriority = AttributePriorityContext.CompareByPriority(a.Mapping, b.Mapping);
        if (byPriority != 0)
            return byPriority;

        var bySyncRule = a.SyncRule.Id.CompareTo(b.SyncRule.Id);
        return bySyncRule != 0 ? bySyncRule : a.TargetAttributeId.CompareTo(b.TargetAttributeId);
    }

    private static void AddToList<TKey>(Dictionary<TKey, List<DerivedFlow>> dictionary, TKey key, DerivedFlow flow) where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out var list))
        {
            list = [];
            dictionary[key] = list;
        }

        list.Add(flow);
    }

    /// <summary>
    /// One Metaverse Object Type's share of the graph.
    /// </summary>
    private sealed class TypeGraph(int metaverseObjectTypeId, string name)
    {
        public int MetaverseObjectTypeId { get; } = metaverseObjectTypeId;
        public string Name { get; } = name;
        public Dictionary<string, MetaverseAttribute> AttributesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, MetaverseAttribute> AttributesById { get; } = new();
        public Dictionary<int, string> AttributeNames { get; } = new();
        public List<DerivedFlow> Flows { get; } = [];
        public Dictionary<int, List<DerivedFlow>> ContributorsByTarget { get; } = new();
        public Dictionary<int, List<DerivedFlow>> ReadersByInput { get; } = new();
        public Dictionary<int, int> Levels { get; } = new();
        public Dictionary<int, List<DerivedFlow>> FlowsByLevel { get; } = new();
        public HashSet<int> Unordered { get; } = [];
        public int MaxLevel { get; set; }
    }

    /// <summary>
    /// Tarjan's strongly connected components over one type's unordered attributes, following "reads" edges. Iterative,
    /// so a long dependency chain cannot exhaust the stack.
    /// </summary>
    private sealed class TarjanComponents(TypeGraph typeGraph)
    {
        private readonly Dictionary<int, int> _index = new();
        private readonly Dictionary<int, int> _lowLink = new();
        private readonly Stack<int> _stack = new();
        private readonly HashSet<int> _onStack = [];
        private readonly List<HashSet<int>> _components = [];
        private int _nextIndex;

        public List<HashSet<int>> Find()
        {
            foreach (var node in typeGraph.Unordered.OrderBy(node => node).Where(node => !_index.ContainsKey(node)))
                Visit(node);

            return _components;
        }

        private List<int> Successors(int node) =>
            ReadsEdges(typeGraph, node, typeGraph.Unordered).Select(edge => edge.Input).Distinct().OrderBy(id => id).ToList();

        private void Visit(int root)
        {
            var work = new Stack<(int Node, List<int> Successors, int Next)>();
            Open(root);
            work.Push((root, Successors(root), 0));

            while (work.Count > 0)
            {
                var (node, successors, next) = work.Pop();
                if (next < successors.Count)
                {
                    work.Push((node, successors, next + 1));
                    var successor = successors[next];
                    if (!_index.ContainsKey(successor))
                    {
                        Open(successor);
                        work.Push((successor, Successors(successor), 0));
                    }
                    else if (_onStack.Contains(successor))
                    {
                        _lowLink[node] = Math.Min(_lowLink[node], _index[successor]);
                    }

                    continue;
                }

                if (_lowLink[node] == _index[node])
                    CloseComponent(node);

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    _lowLink[parent] = Math.Min(_lowLink[parent], _lowLink[node]);
                }
            }
        }

        private void Open(int node)
        {
            _index[node] = _nextIndex;
            _lowLink[node] = _nextIndex;
            _nextIndex++;
            _stack.Push(node);
            _onStack.Add(node);
        }

        private void CloseComponent(int root)
        {
            var component = new HashSet<int>();
            int member;
            do
            {
                member = _stack.Pop();
                _onStack.Remove(member);
                component.Add(member);
            }
            while (member != root);

            _components.Add(component);
        }
    }
}
