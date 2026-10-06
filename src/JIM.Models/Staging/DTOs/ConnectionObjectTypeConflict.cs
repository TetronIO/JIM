// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic.Scoping;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// An enabled export Synchronisation Rule that cannot connect a Metaverse Object to a Connected System because the
/// object's one slot there is held by a Connected System Object of another type (#348, #1331). Reported on the joined
/// connection that holds the slot.
/// </summary>
public sealed class ConnectionObjectTypeConflict
{
    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = string.Empty;

    /// <summary>The Connected System Object Type the rule targets.</summary>
    public string TargetObjectTypeName { get; set; } = string.Empty;

    /// <summary>The type of the Connected System Object already holding the slot.</summary>
    public string ExistingObjectTypeName { get; set; } = string.Empty;

    /// <summary>The conflict in words, generated on the server so every surface says the same thing.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The rule's scoping evaluated against the Metaverse Object now: in scope means synchronisation reports the
    /// conflict on every run; out of scope means the rule would not connect the object even with the slot free.
    /// </summary>
    public ScopingExplanation Scoping { get; set; } = new();
}
