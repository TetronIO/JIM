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
    public ObjectMatchingRuleFailedException(ObjectMatchingRule rule, MatchValueResolution resolution)
        : base(BuildMessage(rule, resolution))
    {
        Rule = rule;
        Resolution = resolution;
    }

    /// <summary>The rule that could not be worked out.</summary>
    public ObjectMatchingRule Rule { get; }

    /// <summary>What the rule computed, carrying the failure message and any expression.</summary>
    public MatchValueResolution Resolution { get; }

    private static string BuildMessage(ObjectMatchingRule rule, MatchValueResolution resolution)
    {
        // Named by what it compares rather than by its order: orders keep their gaps after a rule is deleted.
        var described = rule.TargetMetaverseAttribute == null
            ? "An Object Matching Rule"
            : $"The Object Matching Rule on '{rule.TargetMetaverseAttribute.Name}'";
        var expression = resolution.Expression == null ? string.Empty : $" Expression: {resolution.Expression}";
        return $"{described} could not be evaluated for this object, so it was neither joined nor projected. " +
               $"{resolution.FailureMessage}{expression}";
    }
}
