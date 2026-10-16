// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// A value built from a generated value (Generated Value Forms, #2023): a Metaverse attribute a Derived Attribute Flow
/// builds from it, or an export expression reading it. It changes when the generated value does, and a target's refusal
/// of it is corrected by correcting the generated value, unless its <see cref="KeepUnique"/> switch is off.
/// </summary>
public class GeneratedValueBuiltValueDto
{
    /// <summary>The Attribute Flow that builds the value.</summary>
    public int SyncRuleMappingId { get; set; }

    /// <summary>The Synchronisation Rule holding that Attribute Flow.</summary>
    public int SyncRuleId { get; set; }

    /// <summary>The Synchronisation Rule's name.</summary>
    public string SyncRuleName { get; set; } = null!;

    /// <summary>The generated Attribute Flow the value is built from.</summary>
    public int GeneratedSyncRuleMappingId { get; set; }

    /// <summary>The generated Metaverse attribute the value is built from, for example <c>Account Name</c>.</summary>
    public string GeneratedAttributeName { get; set; } = null!;

    /// <summary>
    /// Every generated Metaverse attribute the value is built from, <see cref="GeneratedAttributeName"/> first. More than
    /// one when <see cref="Kind"/> is <c>BuiltFromSeveralGeneratedValues</c>.
    /// </summary>
    public List<string> BuiltFromAttributeNames { get; set; } = new();

    /// <summary>The Metaverse attribute the value is held in; null for an export expression.</summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>The Connected System attribute an export expression writes; null for a Derived Attribute Flow.</summary>
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// The Metaverse attributes the value is built through, nearest the generated value first: empty for an Email read
    /// straight from Account Name, <c>["Email"]</c> for a User Principal Name built from that Email.
    /// </summary>
    public List<string> Via { get; set; } = new();

    /// <summary>
    /// How the value relates to the generated value: <c>Form</c> (built on the generated value's own Synchronisation
    /// Rule), <c>BuiltOnAnotherRule</c>, or <c>BuiltFromSeveralGeneratedValues</c> (neither checked nor corrected).
    /// </summary>
    public GeneratedValueBuiltValueKind Kind { get; set; }

    /// <summary>
    /// The value's "Keep unique with ..." switch: whether a target's refusal of it is corrected by correcting the
    /// generated value. Set it on the Attribute Flow that builds the value.
    /// </summary>
    public bool KeepUnique { get; set; }

    /// <summary>
    /// The generated Attribute Flow's own <c>collisionRemediation</c> switch. While it is false, nothing built from the
    /// generated value is corrected, whatever <see cref="KeepUnique"/> says.
    /// </summary>
    public bool CollisionRemediation { get; set; }

    /// <summary>Every Connected System attribute the value is written to unchanged.</summary>
    public List<GeneratedValueBuiltValueTargetDto> ExportedTo { get; set; } = new();

    /// <summary>
    /// Whether the value is checked before the generated value is issued: built on the generated flow's own
    /// Synchronisation Rule with <see cref="KeepUnique"/> on, and bound for a Connected System the generated value's
    /// exclusions leave in. A candidate whose value is already held is passed over. False for a value that is only
    /// corrected after a refusal, or neither.
    /// </summary>
    public bool CheckedBeforeIssuing { get; set; }

    /// <summary>
    /// The Connected Systems the value is probed in before the generated value is issued, besides JIM's own records, by
    /// name; empty when it is not checked, or checked against JIM's records alone.
    /// </summary>
    public List<string> ProbedIn { get; set; } = new();

    /// <summary>
    /// For a value built on another Synchronisation Rule whose Attribute Flow reads only Metaverse attributes, the
    /// generated flow's own rule: moving the flow there would have the value checked before issuing. Null otherwise.
    /// </summary>
    public string? MoveToSyncRuleName { get; set; }

    /// <summary>Creates the DTO from the read model.</summary>
    public static GeneratedValueBuiltValueDto FromModel(GeneratedValueBuiltValueDescription model) => new()
    {
        SyncRuleMappingId = model.SyncRuleMappingId,
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName,
        GeneratedSyncRuleMappingId = model.GeneratedSyncRuleMappingId,
        GeneratedAttributeName = model.GeneratedAttributeName,
        BuiltFromAttributeNames = model.BuiltFromAttributeNames.ToList(),
        MetaverseAttributeName = model.MetaverseAttributeName,
        ConnectedSystemAttributeName = model.ConnectedSystemAttributeName,
        Via = model.Via.ToList(),
        Kind = model.Kind,
        KeepUnique = model.KeepUnique,
        CollisionRemediation = model.CollisionRemediation,
        ExportedTo = model.ExportedTo.Select(GeneratedValueBuiltValueTargetDto.FromModel).ToList(),
        CheckedBeforeIssuing = model.CheckedBeforeIssuing,
        ProbedIn = model.ProbedIn.ToList(),
        MoveToSyncRuleName = model.MoveToSyncRuleName
    };
}
