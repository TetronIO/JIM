// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// One Attribute Flow on a <see cref="DerivedFlowAnalysisCycle"/>: it writes <see cref="MetaverseAttributeName"/> from
/// <see cref="ReadsMetaverseAttributeName"/>, which the next link writes.
/// </summary>
/// <param name="MetaverseAttributeName">The Metaverse attribute the flow writes.</param>
/// <param name="ReadsMetaverseAttributeName">The Metaverse attribute on the loop the flow reads.</param>
/// <param name="SyncRuleName">The Synchronisation Rule hosting the flow.</param>
/// <param name="IsAnalysedFlow">Whether this is the flow being analysed (the one the administrator is editing).</param>
public sealed record DerivedFlowAnalysisCycleLink(
    string MetaverseAttributeName,
    string ReadsMetaverseAttributeName,
    string SyncRuleName,
    bool IsAnalysedFlow);
