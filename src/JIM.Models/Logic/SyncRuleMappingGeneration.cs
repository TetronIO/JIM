// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Logic;

/// <summary>
/// Turns a <see cref="SyncRuleMapping"/> into a generated mapping: a one-to-one settings row whose mere presence
/// is the discriminator (<see cref="SyncRuleMapping.GetSourceType"/> returns
/// <see cref="SyncRuleMappingSourcesType.GeneratedMapping"/> when it is set). The mapping's existing
/// <see cref="SyncRuleMapping.Sources"/> still carries the base value as an ordinary expression source; this row
/// only adds the uniqueness token and its settings (Unique Value Generation, #242, plan decision 1). Validated by
/// <see cref="SyncRuleMappingGenerationValidator"/>.
/// </summary>
public class SyncRuleMappingGeneration
{
    public int Id { get; set; }

    /// <summary>
    /// The mapping this settings row belongs to. One-to-one: a mapping is either a generated mapping (this row
    /// exists) or it is not. Cascade delete: removing the mapping, disabling generation, or changing the mapping's
    /// source type removes this row and, through it, every assignment it produced (plan decision 17).
    /// </summary>
    public int SyncRuleMappingId { get; set; }

    public SyncRuleMapping? SyncRuleMapping { get; set; }

    /// <summary>
    /// Which uniqueness token this mapping appends. Defaults to <see cref="GeneratedValueTokenKind.OnlyIfTaken"/>,
    /// the least surprising choice: try the base value first, and only suffix it when necessary.
    /// </summary>
    public GeneratedValueTokenKind TokenKind { get; set; } = GeneratedValueTokenKind.OnlyIfTaken;

    /// <summary>
    /// Only-if-taken: whether the collision suffix is a number or a letter. Ignored for other token kinds.
    /// </summary>
    public GeneratedValueSuffixStyle SuffixStyle { get; set; } = GeneratedValueSuffixStyle.Number;

    /// <summary>
    /// Only-if-taken: the first suffix value tried once the bare base value is taken (at least 1; at most 26 for
    /// <see cref="GeneratedValueSuffixStyle.Letter"/>). Ignored for other token kinds.
    /// </summary>
    public int SuffixStart { get; set; } = 1;

    /// <summary>
    /// Sequence: the lowest number this flow will ever issue. The counter's actual floor
    /// (<see cref="GeneratedValueSequence.NextValue"/>) is whichever of this and the counter's own progress is
    /// higher; raising this value above the counter's current position skips ahead (plan decision 3). Ignored for
    /// other token kinds.
    /// </summary>
    public long SequenceStart { get; set; } = 1;

    /// <summary>
    /// Sequence: how much the counter advances per issued number. Ignored for other token kinds.
    /// </summary>
    public int SequenceIncrement { get; set; } = 1;

    /// <summary>
    /// Sequence: when set, the number is zero-padded to this many digits. Null means no padding. Ignored for
    /// other token kinds.
    /// </summary>
    public int? FixedWidth { get; set; }

    /// <summary>
    /// Sequence: what happens when a number would no longer fit <see cref="FixedWidth"/>. Ignored when
    /// <see cref="FixedWidth"/> is null or for other token kinds.
    /// </summary>
    public GeneratedValueWidthOverflowBehaviour OnWidthExceeded { get; set; } = GeneratedValueWidthOverflowBehaviour.StopAndReport;

    /// <summary>
    /// Random: the shape of the random token. Ignored for other token kinds.
    /// </summary>
    public GeneratedValueRandomFormat RandomFormat { get; set; } = GeneratedValueRandomFormat.Guid;

    /// <summary>
    /// Random: the length of the generated string, in characters. Null for <see cref="GeneratedValueRandomFormat.Guid"/>
    /// (its length is fixed); required for <see cref="GeneratedValueRandomFormat.Hex"/> and
    /// <see cref="GeneratedValueRandomFormat.Digits"/>. Ignored for other token kinds.
    /// </summary>
    public int? RandomLength { get; set; }

    /// <summary>
    /// The characters placed between the base value and the token, when both are present. Null means no
    /// separator. Never offered, and never valid, for a Number target (see
    /// <see cref="SyncRuleMappingGenerationValidator"/>).
    /// </summary>
    public string? Separator { get; set; }

    /// <summary>
    /// The maximum number of candidates tried, per object per synchronisation run, before generation fails hard
    /// for that object (plan decision 15 covers remediation's separate, per-assignment-lifetime counter,
    /// <see cref="GeneratedValueAssignment.RemediationCount"/>).
    /// </summary>
    public int AttemptLimit { get; set; } = 1000;

    /// <summary>
    /// When true (the default), a value whose assignment is deleted is retired and never issued again by this
    /// flow. Always true for <see cref="GeneratedValueTokenKind.Sequence"/>, whose forward-only counter makes
    /// reuse impossible regardless of this setting.
    /// </summary>
    public bool NeverReuse { get; set; } = true;

    /// <summary>
    /// When true (the default), an export rejection JIM's connector classifies as a uniqueness collision on this
    /// value is remediated automatically (release 4): the next candidate is generated, the value revised, and the
    /// object flagged for review. Honoured only where a participating connector classifies uniqueness rejections;
    /// otherwise the rejection is an ordinary export error.
    /// </summary>
    public bool CollisionRemediation { get; set; } = true;

    /// <summary>
    /// Connected Systems excluded from this flow's availability checks: a target the value is deliberately never
    /// checked or reserved against (for example, a system JIM does not export this attribute to, or one known to
    /// enforce uniqueness some other way).
    /// </summary>
    public List<SyncRuleMappingGenerationExclusion> Exclusions { get; } = new();

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the settings were last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Set for the duration of a single create/update call when that save raised the target attribute's
    /// <see cref="GeneratedValueSequence"/> counter because <see cref="SequenceStart"/> stood above its current
    /// position (Phase 3, plan decision 3). Transient: never persisted, and null on every ordinary read. The
    /// server that performed the save stamps it on the same tracked instance it returns, so the caller (a REST
    /// controller, PowerShell) can report the skip in that one response without a second round trip.
    /// </summary>
    [NotMapped]
    public SequenceSkippedAhead? SequenceSkippedAhead { get; set; }
}
