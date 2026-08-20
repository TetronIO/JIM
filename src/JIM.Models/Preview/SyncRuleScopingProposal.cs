// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using JIM.Models.Logic;
using JIM.Models.Search;

namespace JIM.Models.Preview;

/// <summary>
/// The Scoping Criteria an administrator is proposing for a Synchronisation Rule, as the scope change preview
/// adapter receives them (#1436): which objects the rule would manage at all.
/// </summary>
/// <remarks>
/// A dedicated tree rather than the rule's own <see cref="SyncRuleScopingCriteriaGroup"/> graph, for two reasons.
/// A proposal may be evaluated in JIM.Worker, so it has to survive a JSON round trip, and the entity graph cannot:
/// <see cref="SyncRuleScopingCriteriaGroup.ParentGroup"/> and its ChildGroups form a cycle, and each criterion
/// carries a whole attribute entity. And the Scope tab edits the loaded rule's criteria in place, so handing the
/// adapter that graph would give it an object where the proposal has already overwritten the stored scope, and the
/// preview would compare the proposal against itself and report that nothing would change.
/// </remarks>
/// <param name="CriteriaGroups">
/// The top-level criteria groups, combined with OR exactly as the evaluator combines them. Empty means the rule
/// would be unscoped, which is in scope for every object of its type rather than none.
/// </param>
public record SyncRuleScopingProposal(IReadOnlyList<SyncRuleScopingCriteriaGroupProposal> CriteriaGroups)
{
    /// <summary>
    /// Whether this proposal scopes nothing out: no groups at all, or nothing but groups carrying no criteria.
    /// The evaluator treats both as matching every object of the rule's type.
    /// </summary>
    public bool IsUnscoped => CriteriaGroups.Count == 0 || CriteriaGroups.All(group => group.IsEmpty);

    /// <summary>
    /// The scope currently in force on <paramref name="syncRule"/>, as a proposal. What "no change" looks like, and
    /// the baseline an adapter evaluates a proposal against.
    /// </summary>
    public static SyncRuleScopingProposal FromCurrentScope(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        return new SyncRuleScopingProposal(
            [.. syncRule.ObjectScopingCriteriaGroups.Select(SyncRuleScopingCriteriaGroupProposal.FromGroup)]);
    }

    /// <summary>
    /// Whether <paramref name="other"/> proposes the same scope as this one. What decides whether a preview an
    /// administrator is looking at still answers the question they are about to ask.
    /// </summary>
    /// <remarks>
    /// Not the record's own equality: the nested lists are compared by reference by the generated <c>Equals</c>, so
    /// an editor rebuilding its proposal on every render would report a change that never happened. Comparison is
    /// order-insensitive at every level too, because none of the three ways criteria combine (OR across top-level
    /// groups, All within a group, Any within a group) depends on order: dragging one criterion above another
    /// changes what the editor shows and not what the rule matches.
    /// </remarks>
    public bool DescribesSameScopeAs(SyncRuleScopingProposal? other) =>
        other is not null && CanonicalKey() == other.CanonicalKey();

    /// <summary>
    /// An order-insensitive string describing exactly what this proposal matches, for comparison.
    /// </summary>
    private string CanonicalKey() =>
        string.Join("|", CriteriaGroups.Select(group => group.CanonicalKey()).Order(StringComparer.Ordinal));
}

/// <summary>
/// One proposed criteria group: its combining rule, its own criteria, and any groups nested inside it.
/// </summary>
/// <param name="Type">Whether the group's members are combined with All (AND) or Any (OR).</param>
/// <param name="Criteria">The criteria evaluated directly by this group.</param>
/// <param name="ChildGroups">Nested groups, evaluated as further members of this one.</param>
public record SyncRuleScopingCriteriaGroupProposal(
    SearchGroupType Type,
    IReadOnlyList<SyncRuleScopingCriterionProposal> Criteria,
    IReadOnlyList<SyncRuleScopingCriteriaGroupProposal> ChildGroups)
{
    /// <summary>
    /// Whether this group constrains nothing, itself and through its children. An empty group matches everything.
    /// </summary>
    public bool IsEmpty => Criteria.Count == 0 && ChildGroups.All(group => group.IsEmpty);

    /// <summary>
    /// This group as it currently stands on a Synchronisation Rule.
    /// </summary>
    public static SyncRuleScopingCriteriaGroupProposal FromGroup(SyncRuleScopingCriteriaGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return new SyncRuleScopingCriteriaGroupProposal(
            group.Type,
            [.. group.Criteria.Select(SyncRuleScopingCriterionProposal.FromCriterion)],
            [.. group.ChildGroups.Select(FromGroup)]);
    }

    internal string CanonicalKey()
    {
        var members = Criteria.Select(criterion => criterion.CanonicalKey())
            .Concat(ChildGroups.Select(group => group.CanonicalKey()))
            .Order(StringComparer.Ordinal);

        return $"({Type}:{string.Join(",", members)})";
    }
}

/// <summary>
/// One proposed criterion: the attribute it reads, how it compares, and what it compares against.
/// </summary>
/// <remarks>
/// Attributes are carried as ids rather than entities so the proposal stays serialisable. The evaluator reads the
/// attribute entity itself (a criterion whose attribute navigation is null evaluates false, which would silently
/// empty the rule's scope), so whatever materialises a proposal back into a rule must resolve and attach them.
/// </remarks>
/// <param name="MetaverseAttributeId">The Metaverse Attribute read by an export rule's criterion.</param>
/// <param name="ConnectedSystemAttributeId">The Connected System attribute read by an import rule's criterion.</param>
/// <param name="ComparisonType">The comparison applied between the attribute's value and this criterion's value.</param>
/// <param name="StringValue">The value compared against, for a Text attribute.</param>
/// <param name="IntValue">The value compared against, for a Number attribute.</param>
/// <param name="LongValue">The value compared against, for a Long Number attribute.</param>
/// <param name="DecimalValue">The value compared against, for a Decimal attribute.</param>
/// <param name="DateTimeValue">The fixed date compared against, when <paramref name="ValueMode"/> is Absolute.</param>
/// <param name="BoolValue">The value compared against, for a Boolean attribute.</param>
/// <param name="GuidValue">The value compared against, for a Guid attribute.</param>
/// <param name="CaseSensitive">Whether a text comparison respects case.</param>
/// <param name="ValueMode">Whether a date criterion compares against a fixed date or one resolved relative to now.</param>
/// <param name="RelativeCount">The size of the offset from now, when <paramref name="ValueMode"/> is Relative.</param>
/// <param name="RelativeUnit">The unit of the offset from now, when <paramref name="ValueMode"/> is Relative.</param>
/// <param name="RelativeDirection">The direction of the offset from now, when <paramref name="ValueMode"/> is Relative.</param>
public record SyncRuleScopingCriterionProposal(
    int? MetaverseAttributeId,
    int? ConnectedSystemAttributeId,
    SearchComparisonType ComparisonType,
    string? StringValue = null,
    int? IntValue = null,
    long? LongValue = null,
    decimal? DecimalValue = null,
    DateTime? DateTimeValue = null,
    bool? BoolValue = null,
    Guid? GuidValue = null,
    bool CaseSensitive = true,
    DateCriteriaValueMode ValueMode = DateCriteriaValueMode.Absolute,
    int? RelativeCount = null,
    RelativeDateUnit? RelativeUnit = null,
    RelativeDateDirection? RelativeDirection = null)
{
    /// <summary>
    /// This criterion as it currently stands on a Synchronisation Rule.
    /// </summary>
    public static SyncRuleScopingCriterionProposal FromCriterion(SyncRuleScopingCriteria criterion)
    {
        ArgumentNullException.ThrowIfNull(criterion);

        // Each attribute id falls back to its navigation property's id: the Scope editor builds an UNSAVED criterion
        // when an administrator adds one, so the navigation is set and the foreign key stays unassigned until the
        // rule is saved. Reading the key alone made a criterion the editor plainly shows read as naming no
        // attribute, which the preview reports as a blocking finding (#1450).
        return new SyncRuleScopingCriterionProposal(
            criterion.MetaverseAttributeId ?? criterion.MetaverseAttribute?.Id,
            criterion.ConnectedSystemAttributeId ?? criterion.ConnectedSystemAttribute?.Id,
            criterion.ComparisonType,
            criterion.StringValue,
            criterion.IntValue,
            criterion.LongValue,
            criterion.DecimalValue,
            criterion.DateTimeValue,
            criterion.BoolValue,
            criterion.GuidValue,
            criterion.CaseSensitive,
            criterion.ValueMode,
            criterion.RelativeCount,
            criterion.RelativeUnit,
            criterion.RelativeDirection);
    }

    internal string CanonicalKey()
    {
        var key = new StringBuilder();
        key.Append(CultureInfo.InvariantCulture, $"mv={MetaverseAttributeId};cs={ConnectedSystemAttributeId};op={ComparisonType}");
        key.Append(CultureInfo.InvariantCulture, $";s={StringValue};i={IntValue};l={LongValue}");
        key.Append(CultureInfo.InvariantCulture, $";d={DecimalValue?.ToString(CultureInfo.InvariantCulture)}");
        key.Append(CultureInfo.InvariantCulture, $";dt={DateTimeValue?.ToString("O", CultureInfo.InvariantCulture)}");
        key.Append(CultureInfo.InvariantCulture, $";b={BoolValue};g={GuidValue};cse={CaseSensitive}");
        key.Append(CultureInfo.InvariantCulture, $";vm={ValueMode};rc={RelativeCount};ru={RelativeUnit};rd={RelativeDirection}");
        return key.ToString();
    }
}
