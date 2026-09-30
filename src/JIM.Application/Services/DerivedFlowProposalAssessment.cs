// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// What a proposed wholesale save of an import Synchronisation Rule would mean for Metaverse-Derived Attribute Flows
/// (#1750, plan Phase 5), as the Configuration Change Preview reports it.
/// </summary>
/// <param name="Validation">The save-time validation the proposal would face (errors refuse the save).</param>
/// <param name="SyncRulesBefore">Every import rule of the Metaverse Object Type as persisted, disabled included.</param>
/// <param name="SyncRulesAfter">The same rules with the proposal substituted.</param>
/// <param name="MetaverseObjectTypes">The Metaverse Object Type, with its attributes, for resolving <c>mv["..."]</c>.</param>
internal sealed record DerivedFlowProposalAssessment(
    DerivedFlowValidationResult Validation,
    IReadOnlyList<SyncRule> SyncRulesBefore,
    IReadOnlyList<SyncRule> SyncRulesAfter,
    IReadOnlyList<MetaverseObjectType> MetaverseObjectTypes);
