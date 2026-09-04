// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Preview;

namespace JIM.Web.Models;

/// <summary>
/// The previewable settings of a Synchronisation Rule as the editor loaded them, or as it last saved them, so the
/// editor can ask which of its previews have anything to answer.
/// <para>
/// A preview evaluates the form against the stored rule (#1115, #1436, #1437, #1462), so where the two agree the
/// only answer it can give is that nothing changes. Offering it anyway put four buttons beside the save button that
/// all said so; each is now offered only while the settings it answers for differ from this baseline.
/// </para>
/// <para>
/// The four questions are held as the same proposals the previews are asked, and compared the same way, so the
/// button and the preview's own "matches what is stored" finding can never disagree about whether there is an edit.
/// Each is a value snapshot rather than a reference into the rule, which the editor mutates in place, tab by tab.
/// </para>
/// </summary>
public sealed class SyncRulePreviewBaseline
{
    private readonly SyncRuleDestructiveToggleProposal _destructiveToggles;
    private readonly SyncRuleScopingProposal _scope;
    private readonly SyncRuleAttributeFlowProposal _attributeFlow;
    private readonly SyncRuleBehaviourToggleProposal _behaviour;

    private SyncRulePreviewBaseline(SyncRule syncRule)
    {
        _destructiveToggles = SyncRuleDestructiveToggleProposal.FromCurrentSettings(syncRule);
        _scope = SyncRuleScopingProposal.FromCurrentScope(syncRule);
        _attributeFlow = SyncRuleAttributeFlowProposal.FromCurrentMappings(syncRule);
        _behaviour = SyncRuleBehaviourToggleProposal.FromCurrentSettings(syncRule);
    }

    /// <summary>
    /// Records the rule's settings as they stand. Taken when the editor loads a rule and again after each successful
    /// save, since a save keeps the same tracked rule on the page rather than reloading it.
    /// </summary>
    public static SyncRulePreviewBaseline Capture(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        return new SyncRulePreviewBaseline(syncRule);
    }

    /// <summary>
    /// Whether the Deprovisioning Action or Out-of-Scope Action differs from the baseline.
    /// </summary>
    public bool DestructiveTogglesEdited(SyncRule syncRule) =>
        !SyncRuleDestructiveToggleProposal.FromCurrentSettings(syncRule).DescribesSameSettingsAs(_destructiveToggles);

    /// <summary>
    /// Whether the Scoping Criteria differ from the baseline. An unsaved criterion, which names its attribute by
    /// navigation rather than by key until the rule is saved, counts as the edit it plainly is (#1450).
    /// </summary>
    public bool ScopeEdited(SyncRule syncRule) =>
        !SyncRuleScopingProposal.FromCurrentScope(syncRule).DescribesSameScopeAs(_scope);

    /// <summary>
    /// Whether the Attribute Flow mappings differ from the baseline. The recall-or-keep choices staged beside a
    /// removal are not consulted: a choice only exists alongside a staged removal, which is itself an edit.
    /// </summary>
    public bool AttributeFlowEdited(SyncRule syncRule) =>
        !SyncRuleAttributeFlowProposal.FromCurrentMappings(syncRule).DescribesSameMappingsAs(_attributeFlow);

    /// <summary>
    /// Whether any of the behaviour toggles differs from the baseline.
    /// </summary>
    public bool BehaviourEdited(SyncRule syncRule) =>
        !SyncRuleBehaviourToggleProposal.FromCurrentSettings(syncRule).DescribesSameSettingsAs(_behaviour);
}
