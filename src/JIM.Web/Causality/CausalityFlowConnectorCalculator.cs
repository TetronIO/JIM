// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Pure geometry for the Flow view's SVG connector overlay, ported from the approved mock-up's
/// drawFlowLinks algorithm: the source card connects to the first Identity-lane event, and the
/// deepest (last) Identity-lane event connects to each downstream Connected System group; when
/// there are no Identity events the source connects to each group directly. Paths are cubic bezier
/// elbows anchored on card edges, with a terminal dot at the target end.
/// </summary>
public static class CausalityFlowConnectorCalculator
{
    /// <summary>
    /// The flow id of the synthetic source record card.
    /// </summary>
    public const string SourceCardId = "src";

    /// <summary>
    /// Radius of the dot marking a connector's destination end, in pixels. The view reads this
    /// constant for the rendered radius so the geometry and markup cannot drift apart.
    /// </summary>
    public const double TerminalDotRadius = 4.5;

    /// <summary>
    /// Builds the logical connector pairs for a flow layout.
    /// </summary>
    /// <param name="identityEventIds">Flow ids of the Identity-lane events, in tree order.</param>
    /// <param name="systemGroupIds">Flow ids of the downstream Connected System groups, in order.</param>
    public static List<CausalityFlowConnectorPair> BuildConnectorPairs(
        IReadOnlyList<string> identityEventIds,
        IReadOnlyList<string> systemGroupIds)
    {
        var pairs = new List<CausalityFlowConnectorPair>();

        if (identityEventIds.Count > 0)
        {
            pairs.Add(new CausalityFlowConnectorPair(SourceCardId, identityEventIds[0]));

            // The deepest Identity event anchors the fan-out to the downstream system groups
            var anchor = identityEventIds[^1];
            pairs.AddRange(systemGroupIds.Select(groupId => new CausalityFlowConnectorPair(anchor, groupId)));
        }
        else
        {
            pairs.AddRange(systemGroupIds.Select(groupId => new CausalityFlowConnectorPair(SourceCardId, groupId)));
        }

        return pairs;
    }

    /// <summary>
    /// Applies measured card geometry to the logical pairs, producing renderable connectors. Pairs
    /// whose ends were not measured are skipped; connectors are decorative, so missing geometry
    /// degrades to fewer connectors rather than an error.
    /// </summary>
    public static List<CausalityFlowConnector> Compute(
        CausalityFlowMeasurements measurements,
        IReadOnlyList<CausalityFlowConnectorPair> pairs)
    {
        var rectsById = new Dictionary<string, CausalityFlowCardRect>();
        foreach (var card in measurements.Cards.Where(card => !string.IsNullOrEmpty(card.Id)))
            rectsById[card.Id] = card;

        return pairs
            .Select(pair => (
                From: rectsById.GetValueOrDefault(pair.FromId),
                To: rectsById.GetValueOrDefault(pair.ToId)))
            .Where(ends => ends.From != null && ends.To != null)
            .Select(ends => BuildConnector(ends.From!, ends.To!))
            .ToList();
    }

    /// <summary>
    /// Builds one connector between two measured rectangles, anchored on each card's header row with
    /// the elbow's control points at the horizontal midpoint.
    /// </summary>
    private static CausalityFlowConnector BuildConnector(CausalityFlowCardRect from, CausalityFlowCardRect to)
    {
        var x1 = from.Right;
        var y1 = AnchorY(from);
        var x2 = to.Left;
        var y2 = AnchorY(to);
        var midX = (x1 + x2) / 2;

        var pathData = $"M {FormatCoordinate(x1)} {FormatCoordinate(y1)} " +
                       $"C {FormatCoordinate(midX)} {FormatCoordinate(y1)}, " +
                       $"{FormatCoordinate(midX)} {FormatCoordinate(y2)}, " +
                       $"{FormatCoordinate(x2)} {FormatCoordinate(y2)}";
        // The dot is centred on the destination's edge: the overlay renders behind the cards, so the
        // card covers its right half and the line reads as plugging into the card.
        return new CausalityFlowConnector(pathData, FormatCoordinate(x2), FormatCoordinate(y2));
    }

    /// <summary>
    /// The vertical point a connector meets a card: the middle of its header row, which is the row
    /// naming the thing being connected. Header heights differ by card kind (a Connected System
    /// group's chip row is shorter than an event card's icon row), so the previous fixed 34px cap
    /// from the mock-up's drawFlowLinks landed below the middle of both, by a different amount each.
    /// The cap remains as the fallback for a card whose header could not be measured, where landing
    /// near the top is still better than at the card's vertical centre on a tall card.
    /// </summary>
    private static double AnchorY(CausalityFlowCardRect rect)
    {
        return rect.HeaderHeight > 0
            ? rect.HeaderTop + rect.HeaderHeight / 2
            : rect.Top + Math.Min(rect.Height / 2, 34);
    }

    /// <summary>
    /// Formats a coordinate for an SVG attribute: rounded to one decimal place, invariant culture.
    /// </summary>
    public static string FormatCoordinate(double value)
    {
        return Math.Round(value, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
