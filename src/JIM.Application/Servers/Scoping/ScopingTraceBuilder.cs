// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// Builds the explanation tree as <see cref="ScopingEvaluator"/> walks a rule's scoping criteria (#348). Only ever
/// called when an explanation was asked for, so synchronisation never pays for anything here.
/// </summary>
internal static class ScopingTraceBuilder
{
    internal static ScopingGroupExplanation CreateGroup(SyncRuleScopingCriteriaGroup group, string path) => new()
    {
        GroupId = group.Id,
        Path = path,
        Type = group.Type
    };

    /// <summary>Adds the criterion at one-based <paramref name="position"/> among its group's children.</summary>
    internal static ScopingCriterionExplanation AddCriterion(ScopingGroupExplanation parent, SyncRuleScopingCriteria criterion, int position)
    {
        var node = new ScopingCriterionExplanation
        {
            CriterionId = criterion.Id,
            Path = ChildPath(parent, position),
            ComparisonType = criterion.ComparisonType,
            CaseSensitive = criterion.CaseSensitive,
            ValueMode = criterion.ValueMode
        };
        parent.Criteria.Add(node);
        return node;
    }

    /// <summary>Adds the child group at one-based <paramref name="position"/> among its group's children.</summary>
    internal static ScopingGroupExplanation AddChildGroup(ScopingGroupExplanation parent, SyncRuleScopingCriteriaGroup group, int position)
    {
        var node = CreateGroup(group, ChildPath(parent, position));
        parent.ChildGroups.Add(node);
        return node;
    }

    internal static void RecordGroup(ScopingGroupExplanation node, bool? met, int metCount, int childCount)
    {
        node.Met = met;
        node.MetCount = metCount;
        node.ChildCount = childCount;
    }

    /// <summary>
    /// Records what a criterion compared: its attribute, the expected value (for a relative date, the boundary it
    /// resolved to) and the value the object held. Both values are withheld when the attribute may hold a credential.
    /// </summary>
    /// <param name="boundary">The date a DateTime criterion compared against; null for other types, or when the
    /// criterion was not evaluated because its operator is invalid.</param>
    /// <param name="value">The value the outcome turned on: the one that decided it, or a lone value; null when the object
    /// held none, when several values all went the same way, or when the criterion was not evaluated.</param>
    /// <param name="valueCount">How many values the object holds for the attribute.</param>
    internal static void RecordAttribute(ScopingCriterionExplanation node, SyncRuleScopingCriteria criterion, int attributeId,
        AttributeDataType type, string attributeName, DateTime? boundary, ScopingValue? value, int valueCount)
    {
        node.AttributeId = attributeId;
        node.AttributeName = attributeName;
        node.AttributeType = type;
        node.ValueCount = valueCount;

        if (type == AttributeDataType.DateTime && criterion.ValueMode == DateCriteriaValueMode.Relative &&
            criterion.RelativeCount.HasValue && criterion.RelativeUnit.HasValue && criterion.RelativeDirection.HasValue)
        {
            node.RelativeDisplay = RelativeDateResolver.Describe(criterion.RelativeCount.Value, criterion.RelativeUnit.Value, criterion.RelativeDirection.Value);
            node.ResolvedDate = boundary;
        }

        if (IsMasked(attributeName, type))
        {
            node.Masked = true;
            return;
        }

        node.ExpectedDisplay = ScopingValueFormatter.FormatExpected(type, criterion, boundary);
        node.ActualDisplay = value.HasValue ? ScopingValueFormatter.Format(type, value.GetValueOrDefault()) : null;
    }

    /// <summary>
    /// Whether a criterion's values must be withheld: the attribute is on the credential denylist, or its name suggests
    /// a credential and its type could hold one. A credential-like name on a date, number or flag (<c>pwdLastSet</c>,
    /// <c>badPwdCount</c>) cannot carry credential material, and its value is often exactly why a rule scopes someone out.
    /// </summary>
    internal static bool IsMasked(string attributeName, AttributeDataType type) =>
        CredentialAttributes.IsCredentialAttribute(attributeName) ||
        (type is AttributeDataType.Text or AttributeDataType.Binary && CredentialAttributes.HasCredentialLikeName(attributeName));

    private static string ChildPath(ScopingGroupExplanation parent, int position) =>
        string.Concat(parent.Path, ".", position.ToString(CultureInfo.InvariantCulture));
}
