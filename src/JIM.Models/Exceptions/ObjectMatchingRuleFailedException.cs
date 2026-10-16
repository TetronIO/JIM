// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Exceptions;

/// <summary>
/// An Object Matching Rule could not be worked out for an object (#2052): its expression threw, its result could not
/// be converted to the Metaverse attribute's type, or the rule's shape cannot be compared. The object is stopped with
/// an error rather than allowed to match nothing and project or provision a duplicate.
/// </summary>
public class ObjectMatchingRuleFailedException : OperationalException
{
    /// <param name="rule">The rule that could not be worked out.</param>
    /// <param name="resolution">What the rule computed, carrying the failure message.</param>
    /// <param name="provisioning">Whether the rule was matching a Metaverse Object before provisioning it, rather than
    /// a Connected System Object before joining or projecting it; the message names what was stopped.</param>
    public ObjectMatchingRuleFailedException(ObjectMatchingRule rule, MatchValueResolution resolution, bool provisioning = false)
        : base(BuildMessage(rule, resolution, provisioning))
    {
        Rule = rule;
        Resolution = resolution;
    }

    /// <summary>The rule that could not be worked out.</summary>
    public ObjectMatchingRule Rule { get; }

    /// <summary>What the rule computed, carrying the failure message and any expression.</summary>
    public MatchValueResolution Resolution { get; }

    private static string BuildMessage(ObjectMatchingRule rule, MatchValueResolution resolution, bool provisioning)
    {
        // Named by what it compares rather than by its order: orders keep their gaps after a rule is deleted.
        var described = rule.TargetMetaverseAttribute == null
            ? "An Object Matching Rule"
            : $"The Object Matching Rule on '{rule.TargetMetaverseAttribute.Name}'";
        var expression = resolution.Expression == null ? string.Empty : $" Expression: {resolution.Expression}";
        var outcome = provisioning ? "neither joined nor provisioned" : "neither joined nor projected";
        return $"{described} could not be worked out, so this object was {outcome}. " +
               $"{resolution.FailureMessage}{expression}";
    }
}
