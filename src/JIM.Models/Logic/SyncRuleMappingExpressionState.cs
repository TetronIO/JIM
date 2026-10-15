// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// What a stored Attribute Flow's expressions are, and what they flow to (#2055 Phase 3): read untracked on the save
/// path, before anything flushes, to tell a mapping the save changed from one it leaves as it was. Saving refuses any
/// problem with a changed mapping's expressions, and only warns about one it leaves as it was.
/// </summary>
/// <param name="TargetMetaverseAttributeId">The stored import target, or null.</param>
/// <param name="TargetConnectedSystemAttributeId">The stored export target, or null.</param>
/// <param name="Expressions">The stored sources' expressions, in source order, null for a source with none.</param>
public sealed record SyncRuleMappingExpressionState(
    int? TargetMetaverseAttributeId,
    int? TargetConnectedSystemAttributeId,
    IReadOnlyList<string?> Expressions)
{
    /// <summary>
    /// True when <paramref name="mapping"/> flows the same expressions to the same target as is stored.
    /// </summary>
    public bool IsUnchangedIn(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return mapping.ResolveTargetMetaverseAttributeId() == TargetMetaverseAttributeId &&
               mapping.ResolveTargetConnectedSystemAttributeId() == TargetConnectedSystemAttributeId &&
               mapping.Sources.OrderBy(s => s.Order).Select(s => s.Expression).SequenceEqual(Expressions, StringComparer.Ordinal);
    }
}
