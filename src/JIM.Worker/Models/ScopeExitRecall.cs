// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;

namespace JIM.Worker.Models;

/// <summary>
/// What withdrawing a Connected System Object's contributed values did when it left every import Synchronisation Rule's
/// scope: the Metaverse Object attribute changes made, the values withdrawn and any re-elected in their place, the values
/// kept as last known state, and the generated values a re-elected survivor's flows resolved.
/// </summary>
internal sealed record ScopeExitRecall
{
    /// <summary>No recall happened.</summary>
    public static readonly ScopeExitRecall None = new();

    /// <summary>The Metaverse Object attribute changes the recall made, removals and re-elected additions together.</summary>
    public int AttributeChangeCount { get; init; }

    /// <summary>Values kept as last known state because no import source remains for the object's type (#1570).</summary>
    public int PreservedNoSourceAttributeCount { get; init; }

    /// <summary>The withdrawn values, or null when nothing changed.</summary>
    public List<MetaverseObjectAttributeValue>? RecalledAttributeValues { get; init; }

    /// <summary>Values re-elected from a surviving contributor in place of the withdrawn ones, or null when nothing changed.</summary>
    public List<MetaverseObjectAttributeValue>? RecalledAttributeAdditions { get; init; }

    /// <summary>Generated values a re-elected survivor's own flows resolved (#242).</summary>
    public List<(ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string AttributeName, string Value)>? GeneratedValueOutcomes { get; init; }
}
