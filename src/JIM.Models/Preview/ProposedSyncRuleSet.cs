// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Preview;

/// <summary>
/// What a preview proposes about the set of Synchronisation Rules a synchronisation would evaluate: one of them
/// would be different, one would join the set, or one would leave it.
/// </summary>
/// <remarks>
/// The synchronisation preview engine originally took a single proposed rule and substituted it by id, never
/// adding. That says "this rule would be different" and nothing more, which is all a changed scope or a changed
/// mapping needs. It cannot say that a rule starts or stops being evaluated at all, and that is exactly what the
/// Enabled toggle does (#1462): enabling a disabled rule previewed as no change, because a disabled rule is not in
/// the loaded set for the substitution to find, and disabling an enabled one previewed as no change too, because
/// the disabled stand-in stayed in the list and nothing downstream of the load re-checks Enabled.
///
/// Substitution is kept as one of the three cases rather than as a separate concept, so the engine has one notion
/// of what a proposal is and every adapter reaches it the same way.
/// </remarks>
public record ProposedSyncRuleSet
{
    private readonly SyncRule? _rule;
    private readonly int _syncRuleId;
    private readonly ProposedSyncRuleSetKind _kind;

    private ProposedSyncRuleSet(ProposedSyncRuleSetKind kind, SyncRule? rule, int syncRuleId)
    {
        _kind = kind;
        _rule = rule;
        _syncRuleId = syncRuleId;
    }

    /// <summary>
    /// The rule would be different. Replaces the stored rule of the same id, and does nothing when that rule is
    /// not in the set: a rule absent because it is disabled stays absent, because previewing a disabled rule's
    /// proposed scope as though the rule also became enabled would answer a question nobody asked.
    /// </summary>
    public static ProposedSyncRuleSet Substituting(SyncRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new ProposedSyncRuleSet(ProposedSyncRuleSetKind.Substitute, rule, rule.Id);
    }

    /// <summary>
    /// The rule would start being evaluated: what enabling a disabled rule means. Replaces rather than duplicates
    /// where a rule of the same id is already in the set, because a rule evaluated twice contributes twice.
    /// </summary>
    public static ProposedSyncRuleSet Adding(SyncRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new ProposedSyncRuleSet(ProposedSyncRuleSetKind.Add, rule, rule.Id);
    }

    /// <summary>
    /// The rule would stop being evaluated: what disabling a rule, or deleting it, means.
    /// </summary>
    public static ProposedSyncRuleSet Removing(int syncRuleId) =>
        new(ProposedSyncRuleSetKind.Remove, null, syncRuleId);

    /// <summary>
    /// Applies this proposal to the rule set the engine loaded, in place.
    /// </summary>
    /// <param name="syncRules">The rules a synchronisation would evaluate, as loaded from the database.</param>
    public void Apply(List<SyncRule> syncRules)
    {
        ArgumentNullException.ThrowIfNull(syncRules);

        var storedIndex = syncRules.FindIndex(rule => rule.Id == _syncRuleId);

        switch (_kind)
        {
            case ProposedSyncRuleSetKind.Substitute:
                if (storedIndex >= 0)
                    syncRules[storedIndex] = _rule!;
                break;

            case ProposedSyncRuleSetKind.Add:
                if (storedIndex >= 0)
                    syncRules[storedIndex] = _rule!;
                else
                    syncRules.Add(_rule!);
                break;

            case ProposedSyncRuleSetKind.Remove:
            default:
                if (storedIndex >= 0)
                    syncRules.RemoveAt(storedIndex);
                break;
        }
    }

    private enum ProposedSyncRuleSetKind
    {
        Substitute,
        Add,
        Remove
    }
}
