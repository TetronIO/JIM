// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;
using JIM.Models.Logic;

namespace JIM.Web.Models;

/// <summary>
/// The one-line summary each collapsible section of the Attribute Flow dialog shows while it is closed (#2471), so that
/// collapsing a section never hides what it is set to. Each names the choice as the form offers it.
/// </summary>
public static class AttributeFlowSectionSummaries
{
    /// <summary>The uniqueness token a Generated Value adds.</summary>
    public static string UniquenessToken(GeneratedValueTokenKind kind) => kind switch
    {
        GeneratedValueTokenKind.Sequence => "Always add a sequence number",
        GeneratedValueTokenKind.Random => "Always add a random token",
        _ => "Add a number only if taken"
    };

    /// <summary>
    /// The Missing Input Behaviour. A Generated Value offers "Contribute no value" as waiting, since a value generated
    /// from a missing input would be kept for ever.
    /// </summary>
    public static string MissingInput(MissingInputBehaviour behaviour, bool generated) => behaviour switch
    {
        MissingInputBehaviour.ContributeNoValue => generated ? "Wait until every input has a value" : "Contribute no value",
        MissingInputBehaviour.FailMapping => "Fail this mapping",
        MissingInputBehaviour.FailObject => "Fail the object",
        _ => "Evaluate anyway"
    };

    /// <summary>Where a Generated Value is checked for availability; null while the Connected Systems load.</summary>
    public static string Availability(IReadOnlyList<GeneratedValueParticipant>? participants)
    {
        if (participants == null)
            return "Loading";
        if (participants.Count == 0)
            return "Not exported to any Connected System";

        var checkedCount = participants.Count(participant => !participant.IsExcluded);
        var excludedCount = participants.Count - checkedCount;
        var summary = $"Checked in {checkedCount} Connected System{(checkedCount == 1 ? string.Empty : "s")}";
        return excludedCount > 0 ? $"{summary}, {excludedCount} excluded" : summary;
    }

    /// <summary>
    /// Collision Remediation, which reads off while no system the value is exported to can report a collision,
    /// whatever is stored.
    /// </summary>
    public static string CollisionRemediation(SyncRuleMappingGeneration generation, IReadOnlyList<GeneratedValueParticipant>? participants)
    {
        if (participants?.Any(participant => participant.ReportsCollisions) != true)
            return "Not available";
        return generation.CollisionRemediation ? "On" : "Off";
    }

    /// <summary>
    /// How an import Attribute Flow treats no value and processes text: "Null is a value", then each value processing
    /// step that is on. Value processing applies to Text attributes only.
    /// </summary>
    public static string ValueHandling(SyncRuleMapping mapping, bool textTarget)
    {
        var parts = new List<string>();
        if (mapping.NullIsValue)
            parts.Add("Null is a value");

        if (textTarget)
        {
            if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.TreatWhitespaceAsNoValue))
                parts.Add("Treat whitespace as no value");
            if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.TrimWhitespace))
                parts.Add("Trim whitespace");
            if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.CollapseInternalWhitespace))
                parts.Add("Collapse internal whitespace");
            if (mapping.CaseNormalisation != InboundCaseNormalisation.None)
                parts.Add($"{mapping.CaseNormalisation} case");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "Null is not a value";
    }
}
