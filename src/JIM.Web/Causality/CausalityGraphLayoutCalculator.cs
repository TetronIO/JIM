// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Pure layout for the Graph view, ported exactly from the approved mock-up's renderGraph
/// algorithm: a layered node-link diagram where depth maps to an x column, leaves take successive
/// y rows, parents centre over their children, and a synthetic "Source record" root at depth 0
/// centres over the root events. Edges are cubic beziers from the right centre of the parent to
/// the left centre of the child, formatted in invariant culture. Title and sub truncation happens
/// here so it is unit-testable.
/// </summary>
public static class CausalityGraphLayoutCalculator
{
    /// <summary>
    /// The node id of the synthetic source-record root.
    /// </summary>
    public const string SourceNodeId = "src";

    /// <summary>
    /// Node rectangle width.
    /// </summary>
    public const double NodeWidth = 210;

    /// <summary>
    /// Node rectangle height.
    /// </summary>
    public const double NodeHeight = 58;

    /// <summary>
    /// Horizontal gap between depth columns.
    /// </summary>
    public const double ColumnGap = 70;

    /// <summary>
    /// Vertical gap between leaf rows.
    /// </summary>
    public const double RowGap = 26;

    /// <summary>
    /// Maximum node title length before truncation.
    /// </summary>
    public const int TitleMaxLength = 26;

    /// <summary>
    /// Maximum node sub-line length before truncation.
    /// </summary>
    public const int SubMaxLength = 30;

    /// <summary>
    /// The entity link kinds whose labels can serve as a node's sub line; mirrors the kinds the
    /// event card renders as entity chips (the remaining kinds are footer action links).
    /// </summary>
    private static readonly CausalityEntityKind[] SubEntityKinds =
    [
        CausalityEntityKind.ConnectedSystem,
        CausalityEntityKind.Record,
        CausalityEntityKind.Identity,
        CausalityEntityKind.SynchronisationRule
    ];

    /// <summary>
    /// Computes the Graph view layout for a causality model: positioned nodes (events in tree
    /// order, then the synthetic source root), bezier edges and the overall canvas size.
    /// </summary>
    /// <param name="model">The causality model to lay out.</param>
    /// <param name="technicalNames">When true, node titles use the technical labels.</param>
    public static CausalityGraphLayout Compute(CausalityModel model, bool technicalNames)
    {
        var workingNodes = new List<WorkingNode>();
        var edgePairs = new List<(string FromId, string ToId)>();
        var nextY = 0d;
        var eventIndex = 0;

        // Depth-first: each node is emitted (and its edge from the parent recorded) before its
        // children; leaves take the next y row and parents centre over their children afterwards
        double Layout(CausalityEvent causalityEvent, int depth, string parentId)
        {
            var node = new WorkingNode(
                $"evt-{eventIndex++}",
                depth,
                Truncate(technicalNames ? causalityEvent.TechnicalLabel : causalityEvent.PlainLabel, TitleMaxLength),
                Truncate(GetSub(causalityEvent), SubMaxLength),
                causalityEvent.Tone,
                causalityEvent.AttributeRows.Count > 0,
                causalityEvent);
            workingNodes.Add(node);
            edgePairs.Add((parentId, node.Id));

            if (causalityEvent.Children.Count > 0)
            {
                var childYs = causalityEvent.Children.Select(child => Layout(child, depth + 1, node.Id)).ToList();
                node.LayoutY = (childYs[0] + childYs[^1]) / 2;
            }
            else
            {
                node.LayoutY = nextY;
                nextY += NodeHeight + RowGap;
            }

            return node.LayoutY;
        }

        var rootYs = model.Roots.Select(root => Layout(root, 1, SourceNodeId)).ToList();

        var sourceNode = new WorkingNode(
            SourceNodeId,
            0,
            Truncate("Source record", TitleMaxLength),
            Truncate(GetSourceLabel(model.Context), SubMaxLength),
            CausalityTone.Secondary,
            hasAttributeRows: false,
            causalityEvent: null)
        {
            LayoutY = rootYs.Count > 0 ? (rootYs[0] + rootYs[^1]) / 2 : 0
        };
        workingNodes.Add(sourceNode);

        var maxDepth = workingNodes.Max(n => n.Depth);
        var width = (maxDepth + 1) * (NodeWidth + ColumnGap) - ColumnGap + 4;
        var height = Math.Max(nextY - RowGap, NodeHeight) + 4;

        var nodes = workingNodes
            .Select(n => new CausalityGraphNode(
                n.Id, n.Depth,
                n.Depth * (NodeWidth + ColumnGap) + 2,
                n.LayoutY + 2,
                n.Title, n.Sub, n.Tone, n.HasAttributeRows, n.Event))
            .ToList();
        var nodesById = nodes.ToDictionary(n => n.Id);
        var edges = edgePairs
            .Select(pair => new CausalityGraphEdge(
                pair.FromId, pair.ToId,
                BuildEdgePath(nodesById[pair.FromId], nodesById[pair.ToId])))
            .ToList();

        return new CausalityGraphLayout(nodes, edges, width, height);
    }

    /// <summary>
    /// Truncates a display string to a maximum length, replacing the overflow with a single
    /// ellipsis character so the result never exceeds the maximum. Null yields an empty string.
    /// </summary>
    public static string Truncate(string? value, int maxLength)
    {
        if (value == null)
            return string.Empty;

        return value.Length > maxLength ? value[..(maxLength - 1)] + "…" : value;
    }

    /// <summary>
    /// Builds the cubic bezier path from the right centre of the parent node to the left centre of
    /// the child node, with the control points at the horizontal midpoint.
    /// </summary>
    private static string BuildEdgePath(CausalityGraphNode from, CausalityGraphNode to)
    {
        var x1 = from.X + NodeWidth;
        var y1 = from.Y + NodeHeight / 2;
        var x2 = to.X;
        var y2 = to.Y + NodeHeight / 2;
        var midX = (x1 + x2) / 2;

        return $"M {Format(x1)} {Format(y1)} " +
               $"C {Format(midX)} {Format(y1)}, {Format(midX)} {Format(y2)}, {Format(x2)} {Format(y2)}";
    }

    /// <summary>
    /// A node's sub line: the attribute count when the event carries attribute detail, else the
    /// first entity chip label, else the owning system name, else empty.
    /// </summary>
    private static string GetSub(CausalityEvent causalityEvent)
    {
        if (causalityEvent.AttributeRows.Count > 0)
        {
            var count = causalityEvent.AttributeRows.Count;
            return $"{count} attribute{(count == 1 ? "" : "s")}";
        }

        var entityLink = causalityEvent.Links.FirstOrDefault(link => SubEntityKinds.Contains(link.Kind));
        if (entityLink != null)
            return entityLink.Label;

        return causalityEvent.SystemName ?? string.Empty;
    }

    /// <summary>
    /// The source root's sub line: the record's display name and external id (whichever are
    /// available), falling back to the Connected System name for records without either.
    /// </summary>
    private static string GetSourceLabel(CausalityPageContext context)
    {
        var hasDisplayName = !string.IsNullOrWhiteSpace(context.CsoDisplayName);
        var hasExternalId = !string.IsNullOrWhiteSpace(context.CsoExternalId);

        if (hasDisplayName && hasExternalId)
            return $"{context.CsoDisplayName} ({context.CsoExternalId})";
        if (hasDisplayName)
            return context.CsoDisplayName!;
        if (hasExternalId)
            return context.CsoExternalId!;
        return context.ConnectedSystemName ?? string.Empty;
    }

    /// <summary>
    /// Formats a coordinate for an SVG attribute: rounded to one decimal place, invariant culture.
    /// </summary>
    private static string Format(double value)
    {
        return CausalityFlowConnectorCalculator.FormatCoordinate(value);
    }

    /// <summary>
    /// Mutable working node used while the recursion resolves y positions (parents are emitted
    /// before their children but centred afterwards); mapped to immutable records once complete.
    /// </summary>
    private sealed class WorkingNode(
        string id, int depth, string title, string sub, CausalityTone tone,
        bool hasAttributeRows, CausalityEvent? causalityEvent)
    {
        public string Id { get; } = id;

        public int Depth { get; } = depth;

        public string Title { get; } = title;

        public string Sub { get; } = sub;

        public CausalityTone Tone { get; } = tone;

        public bool HasAttributeRows { get; } = hasAttributeRows;

        public CausalityEvent? Event { get; } = causalityEvent;

        public double LayoutY { get; set; }
    }
}
