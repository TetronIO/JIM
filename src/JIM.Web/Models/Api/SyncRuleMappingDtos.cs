// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// An Attribute Flow on a Synchronisation Rule: what it reads, what it writes, and how.
/// </summary>
public class SyncRuleMappingDto
{
    /// <summary>
    /// The ID of the mapping.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// When the mapping was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The ID of the Metaverse attribute an import mapping writes; null on an export mapping.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of that Metaverse attribute; null on an export mapping.
    /// </summary>
    public string? TargetMetaverseAttributeName { get; set; }

    /// <summary>
    /// The ID of the Connected System attribute an export mapping writes; null on an import mapping.
    /// </summary>
    public int? TargetConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The name of that Connected System attribute; null on an import mapping.
    /// </summary>
    public string? TargetConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// What the mapping reads: AttributeMapping for attributes, ExpressionMapping for an expression,
    /// AdvancedMapping for a mix of the two, GeneratedMapping for a generated value, or NotSet when it has no
    /// source.
    /// </summary>
    public string SourceType { get; set; } = null!;

    /// <summary>
    /// The text processing an import mapping applies on the way into the Metaverse, as a comma-separated set of
    /// flags such as TreatWhitespaceAsNoValue and TrimWhitespace. Only meaningful on an import mapping to a Text
    /// attribute.
    /// </summary>
    public InboundValueProcessing InboundValueProcessing { get; set; }

    /// <summary>
    /// The case an import mapping converts a text value to. Only meaningful on an
    /// import mapping to a Text attribute.
    /// </summary>
    public InboundCaseNormalisation CaseNormalisation { get; set; }

    /// <summary>
    /// The mapping's place in its attribute's Attribute Priority order: the lowest number with a value wins.
    /// 2147483647 marks a mapping not yet put in order, which never wins. Only meaningful on an import mapping.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether an import mapping that applies to a joined, in-scope object but has no value clears the Metaverse
    /// attribute, rather than letting a lower-priority mapping supply one. Only meaningful on an import mapping.
    /// </summary>
    public bool NullIsValue { get; set; }

    /// <summary>
    /// Whether an export mapping writes only when it creates the Connected System Object; after that JIM leaves the
    /// attribute alone, and Drift Correction does not put it back. Only meaningful on an export mapping.
    /// </summary>
    public bool InitialExportOnly { get; set; }

    /// <summary>
    /// Whether the mapping takes part in synchronisation; a disabled mapping is skipped in both directions.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Why JIM disabled the mapping, when it did so itself; null when the mapping is enabled, or an administrator
    /// disabled it.
    /// </summary>
    public string? DisabledReason { get; set; }

    /// <summary>
    /// On an Expression mapping whose value is built from one generated value, whether a target refusing the value
    /// as already in use is fixed by correcting the generated value. True by default; no effect on any other
    /// mapping.
    /// </summary>
    public bool KeepUnique { get; set; }

    /// <summary>
    /// On an Expression mapping whose value is built from a generated value: which one, through which attributes,
    /// and how JIM treats it. Null on every other mapping.
    /// </summary>
    public GeneratedValueBuiltValueDto? BuiltFromGeneratedValue { get; set; }

    /// <summary>
    /// What the mapping reads: the attributes or expression its value comes from. Empty on a generated mapping with
    /// no base expression.
    /// </summary>
    public List<SyncRuleMappingSourceDto> Sources { get; set; } = new();

    /// <summary>
    /// The settings of a generated mapping: its token, how it is checked for availability, and what is built from
    /// it. Null unless SourceType is GeneratedMapping.
    /// </summary>
    public SyncRuleMappingGenerationDto? Generation { get; set; }

    /// <summary>
    /// Warnings the create or update raised about the mapping without stopping it, such as a derived Attribute Flow
    /// calling a function that returns a different value each time. Empty on a read.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// On a Metaverse-Derived Attribute Flow (an import expression reading mv["..."]): the Metaverse attributes it
    /// reads and its step in the evaluation order. Null on every other mapping.
    /// </summary>
    public DerivedFlowInfoDto? Derived { get; set; }

    /// <summary>
    /// On the response to an update that disabled the mapping, rewrote its expression or changed what it writes:
    /// the Metaverse-Derived Attribute Flows the change left with a missing input. The update went ahead; each
    /// flow's Missing Input Behaviour decides what it contributes. Empty on a read.
    /// </summary>
    public List<DependentDerivedFlow> DependentDerivedFlows { get; set; } = new();

    public static SyncRuleMappingDto FromEntity(SyncRuleMapping entity)
    {
        return new SyncRuleMappingDto
        {
            Id = entity.Id,
            Created = entity.Created,
            TargetMetaverseAttributeId = entity.TargetMetaverseAttributeId,
            TargetMetaverseAttributeName = entity.TargetMetaverseAttribute?.Name,
            TargetConnectedSystemAttributeId = entity.TargetConnectedSystemAttributeId,
            TargetConnectedSystemAttributeName = entity.TargetConnectedSystemAttribute?.Name,
            SourceType = entity.GetSourceType().ToString(),
            InboundValueProcessing = entity.InboundValueProcessing,
            CaseNormalisation = entity.CaseNormalisation,
            Priority = entity.Priority,
            NullIsValue = entity.NullIsValue,
            InitialExportOnly = entity.InitialExportOnly,
            Enabled = entity.Enabled,
            DisabledReason = entity.DisabledReason,
            KeepUnique = entity.KeepUnique,
            Sources = entity.Sources.Select(SyncRuleMappingSourceDto.FromEntity).ToList(),
            Generation = entity.Generation == null ? null : SyncRuleMappingGenerationDto.FromEntity(entity.Generation),
            Warnings = entity.SaveWarnings.ToList(),
            DependentDerivedFlows = entity.SaveDependentDerivedFlows.ToList()
        };
    }
}

/// <summary>
/// A generated mapping's settings: the token it appends, where its value is checked for availability, whether a
/// rejected value is corrected, and what is built from it.
/// </summary>
public class SyncRuleMappingGenerationDto
{
    /// <summary>
    /// The token appended to the base value: OnlyIfTaken adds a suffix only when the base value is taken, Sequence
    /// adds the next number from the attribute's counter, and Random adds a random token.
    /// </summary>
    public GeneratedValueTokenKind TokenKind { get; set; }

    /// <summary>
    /// For OnlyIfTaken, whether the suffix is a Number or a Letter.
    /// </summary>
    public GeneratedValueSuffixStyle SuffixStyle { get; set; }

    /// <summary>
    /// For OnlyIfTaken, the first suffix tried once the base value is taken.
    /// </summary>
    public int SuffixStart { get; set; }

    /// <summary>
    /// For Sequence, the lowest number the mapping issues.
    /// </summary>
    public long SequenceStart { get; set; }

    /// <summary>
    /// For Sequence, how far the counter moves for each number issued.
    /// </summary>
    public int SequenceIncrement { get; set; }

    /// <summary>
    /// For Sequence, how many digits the number is zero-padded to; null for no padding.
    /// </summary>
    public int? FixedWidth { get; set; }

    /// <summary>
    /// For Sequence with a FixedWidth, what happens when a number no longer fits: StopAndReport stops the object
    /// with an error, and AllowLonger issues the longer number.
    /// </summary>
    public GeneratedValueWidthOverflowBehaviour OnWidthExceeded { get; set; }

    /// <summary>
    /// For Random, the shape of the token: Guid, Hex or Digits.
    /// </summary>
    public GeneratedValueRandomFormat RandomFormat { get; set; }

    /// <summary>
    /// For Random with Hex or Digits, the length of the token in characters; null for Guid.
    /// </summary>
    public int? RandomLength { get; set; }

    /// <summary>
    /// What goes between the base value and the token, when both are present; null for nothing.
    /// </summary>
    public string? Separator { get; set; }

    /// <summary>
    /// How many candidates are tried for one object in one synchronisation run before generation fails for it.
    /// </summary>
    public int AttemptLimit { get; set; }

    /// <summary>
    /// Whether a value is retired, and never issued again, once the object holding it no longer does. Always true
    /// for Sequence.
    /// </summary>
    public bool NeverReuse { get; set; }

    /// <summary>
    /// Whether JIM corrects a value a target rejects as already in use. While no other Connected System holds the
    /// value, JIM chooses the next free one and exports it again; once another does, the export is held for a
    /// decision. It acts only on a Connected System whose Connector reports collisions; elsewhere a collision is an
    /// ordinary export error.
    /// </summary>
    public bool CollisionRemediation { get; set; }

    /// <summary>
    /// On the response to a create or update that moved the attribute's counter forward to SequenceStart: where it
    /// moved from and to. Null on a read.
    /// </summary>
    public SequenceSkippedAheadDto? SequenceSkippedAhead { get; set; }

    /// <summary>
    /// How many retired values the attribute holds: values JIM will never issue for it again. Present on a read;
    /// null on the response to a create or update.
    /// </summary>
    public int? RetiredValueCount { get; set; }

    /// <summary>
    /// The IDs of the Connected Systems left out of the availability checks, in ascending order: a value already in
    /// use there does not stop JIM choosing it. Empty when none is.
    /// </summary>
    public List<int> Exclusions { get; set; } = new();

    /// <summary>
    /// Every Connected System attribute the value is exported to, and how its availability is checked there,
    /// ordered by Connected System name, then attribute name.
    /// </summary>
    public List<GeneratedValueParticipantDto>? Participants { get; set; }

    /// <summary>
    /// Every value built from this generated value: the derived Attribute Flows and export expressions that read
    /// it, on any Synchronisation Rule. Present on an import mapping.
    /// </summary>
    public List<GeneratedValueBuiltValueDto>? BuiltValues { get; set; }

    public static SyncRuleMappingGenerationDto FromEntity(SyncRuleMappingGeneration entity) => new()
    {
        Exclusions = entity.Exclusions.Select(e => e.ConnectedSystemId).Order().ToList(),
        TokenKind = entity.TokenKind,
        SuffixStyle = entity.SuffixStyle,
        SuffixStart = entity.SuffixStart,
        SequenceStart = entity.SequenceStart,
        SequenceIncrement = entity.SequenceIncrement,
        FixedWidth = entity.FixedWidth,
        OnWidthExceeded = entity.OnWidthExceeded,
        RandomFormat = entity.RandomFormat,
        RandomLength = entity.RandomLength,
        Separator = entity.Separator,
        AttemptLimit = entity.AttemptLimit,
        NeverReuse = entity.NeverReuse,
        CollisionRemediation = entity.CollisionRemediation,
        SequenceSkippedAhead = entity.SequenceSkippedAhead == null
            ? null
            : new SequenceSkippedAheadDto { From = entity.SequenceSkippedAhead.From, To = entity.SequenceSkippedAhead.To }
    };
}

/// <summary>
/// How far a save moved a Sequence mapping's counter forward.
/// </summary>
public class SequenceSkippedAheadDto
{
    /// <summary>
    /// The next number before the save.
    /// </summary>
    public long From { get; set; }

    /// <summary>
    /// The next number after the save: the Sequence Start.
    /// </summary>
    public long To { get; set; }
}

/// <summary>
/// Request DTO for a generated mapping's uniqueness token settings, on creation (Unique Value Generation, #242,
/// Phase 3). Server-side validation (<see cref="SyncRuleMappingGenerationValidator"/>) decides what combination
/// of these is actually valid for a given target attribute and token kind; this DTO only carries the values
/// through.
/// </summary>
public class CreateSyncRuleMappingGenerationRequest
{
    public GeneratedValueTokenKind TokenKind { get; set; }
    public GeneratedValueSuffixStyle SuffixStyle { get; set; } = GeneratedValueSuffixStyle.Number;
    public int SuffixStart { get; set; } = 1;
    public long SequenceStart { get; set; } = 1;
    public int SequenceIncrement { get; set; } = 1;
    public int? FixedWidth { get; set; }
    public GeneratedValueWidthOverflowBehaviour OnWidthExceeded { get; set; } = GeneratedValueWidthOverflowBehaviour.StopAndReport;
    public GeneratedValueRandomFormat RandomFormat { get; set; } = GeneratedValueRandomFormat.Guid;
    public int? RandomLength { get; set; }
    public string? Separator { get; set; }
    public int AttemptLimit { get; set; } = 1000;
    public bool NeverReuse { get; set; } = true;

    /// <summary>
    /// Whether JIM corrects a value a target rejects as already in use (Collision Remediation, release 4). Defaults to on.
    /// </summary>
    public bool CollisionRemediation { get; set; } = true;

    /// <summary>
    /// The ids of the Connected Systems to exclude from the value's availability checks (release 3). Each must be a
    /// Connected System the value is exported to unchanged, by an export Attribute Flow taking the generated Metaverse
    /// attribute as its only source; otherwise the request is refused naming the Connected System. Not accepted for a
    /// generated value on an export Synchronisation Rule, which is checked only in its own Connected System. Omit or
    /// leave empty to check every Connected System.
    /// </summary>
    public List<int>? Exclusions { get; set; }

    public SyncRuleMappingGeneration ToEntity()
    {
        var generation = ToEntityWithoutExclusions();
        generation.Exclusions.AddRange((Exclusions ?? [])
            .Select(connectedSystemId => new SyncRuleMappingGenerationExclusion { ConnectedSystemId = connectedSystemId }));
        return generation;
    }

    private SyncRuleMappingGeneration ToEntityWithoutExclusions() => new()
    {
        TokenKind = TokenKind,
        SuffixStyle = SuffixStyle,
        SuffixStart = SuffixStart,
        SequenceStart = SequenceStart,
        SequenceIncrement = SequenceIncrement,
        FixedWidth = FixedWidth,
        OnWidthExceeded = OnWidthExceeded,
        RandomFormat = RandomFormat,
        RandomLength = RandomLength,
        Separator = Separator,
        AttemptLimit = AttemptLimit,
        NeverReuse = NeverReuse,
        CollisionRemediation = CollisionRemediation
    };
}

/// <summary>
/// Request DTO for changing an existing generated mapping's uniqueness token settings (Unique Value Generation,
/// #242, Phase 3). Every field is optional and an omitted one leaves the mapping's current value alone, matching
/// <see cref="UpdateSyncRuleMappingRequest"/>'s own convention. Applying this to a mapping that is not currently
/// a generated mapping is refused; turning one into the other is not supported here (delete and create).
/// </summary>
public class UpdateSyncRuleMappingGenerationRequest
{
    public GeneratedValueTokenKind? TokenKind { get; set; }
    public GeneratedValueSuffixStyle? SuffixStyle { get; set; }
    public int? SuffixStart { get; set; }

    /// <summary>
    /// Raising this above the target attribute's counter moves the counter forward at save time (plan decision
    /// 3); the response's <see cref="SyncRuleMappingGenerationDto.SequenceSkippedAhead"/> reports the move. A
    /// lower or equal value has no effect.
    /// </summary>
    public long? SequenceStart { get; set; }

    public int? SequenceIncrement { get; set; }

    /// <summary><c>0</c> clears the fixed width; a positive value sets it; omitted (null) leaves it unchanged.</summary>
    public int? FixedWidth { get; set; }

    public GeneratedValueWidthOverflowBehaviour? OnWidthExceeded { get; set; }
    public GeneratedValueRandomFormat? RandomFormat { get; set; }
    public int? RandomLength { get; set; }

    /// <summary>An empty or whitespace-only string clears the stored separator; omitted (null) leaves it unchanged.</summary>
    public string? Separator { get; set; }

    public int? AttemptLimit { get; set; }
    public bool? NeverReuse { get; set; }

    /// <summary>
    /// Whether JIM corrects a value a target rejects as already in use (Collision Remediation, release 4). Omitted (null)
    /// leaves it unchanged. Changing it releases any values of this flow held for a decision, so the next export tries
    /// again under the new setting.
    /// </summary>
    public bool? CollisionRemediation { get; set; }

    /// <summary>
    /// The ids of the Connected Systems excluded from the value's availability checks (release 3). Omitted (null)
    /// leaves the exclusions unchanged; an empty list clears them; a list replaces them. Each must be a Connected System
    /// the value is exported to unchanged, otherwise the request is refused naming the Connected System; refused
    /// outright for a generated value on an export Synchronisation Rule.
    /// </summary>
    public List<int>? Exclusions { get; set; }

    public SyncRuleMappingGenerationSettingsUpdate ToSettingsUpdate() => new()
    {
        Exclusions = Exclusions,
        TokenKind = TokenKind,
        SuffixStyle = SuffixStyle,
        SuffixStart = SuffixStart,
        SequenceStart = SequenceStart,
        SequenceIncrement = SequenceIncrement,
        FixedWidth = FixedWidth,
        OnWidthExceeded = OnWidthExceeded,
        RandomFormat = RandomFormat,
        RandomLength = RandomLength,
        Separator = Separator,
        AttemptLimit = AttemptLimit,
        NeverReuse = NeverReuse,
        CollisionRemediation = CollisionRemediation
    };
}

/// <summary>
/// Request to change the settings on an existing Attribute Flow.
/// </summary>
/// <remarks>
/// Every field is optional and an omitted one leaves the mapping's current value alone; a request naming no
/// field at all is rejected rather than answered as a successful no-op. What the mapping targets, and whether
/// its source is an attribute or an Expression, cannot be changed here: those revalidate against attribute types
/// and reopen an import mapping's Attribute Priority position, so they remain a delete and a create.
/// </remarks>
public class UpdateSyncRuleMappingRequest
{
    /// <summary>
    /// Replaces the mapping's Expression. Expression mappings only; rejected for an attribute mapping, and for a
    /// mapping carrying more than one Expression source.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// What the Expression does when an attribute it reads has no value on the object being synchronised.
    /// Expression mappings only.
    /// </summary>
    public MissingInputBehaviour? MissingInputBehaviour { get; set; }

    /// <summary>
    /// Whether a contribution of no value from this mapping is authoritative ("Null is a value"). Import
    /// mappings only.
    /// </summary>
    public bool? NullIsValue { get; set; }

    /// <summary>
    /// Text value-processing transforms applied as the value flows to the Metaverse. Import mappings only.
    /// </summary>
    public InboundValueProcessing? InboundValueProcessing { get; set; }

    /// <summary>
    /// Case normalisation applied as the value flows to the Metaverse. Import mappings only.
    /// </summary>
    public InboundCaseNormalisation? CaseNormalisation { get; set; }

    /// <summary>
    /// Whether the mapping flows only during the initial provisioning export. Export mappings only.
    /// </summary>
    public bool? InitialExportOnly { get; set; }

    /// <summary>
    /// Enables or disables the mapping (#1485). A disabled mapping is skipped by synchronisation in both
    /// directions until it is re-enabled; re-enabling clears any recorded disabled reason. Applies to import
    /// and export mappings alike.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// The "Keep unique with ..." switch (Generated Value Forms, #2023): whether a target's refusal of a value built from a
    /// generated value is corrected by correcting the generated value. Expression mappings only; refused for an attribute
    /// mapping and for a generated mapping. Changing it releases any decision the generated value is waiting on.
    /// </summary>
    public bool? KeepUnique { get; set; }

    /// <summary>
    /// Changes to a generated mapping's uniqueness token settings (Unique Value Generation, #242, Phase 3).
    /// Refused for a mapping that is not currently a generated mapping.
    /// </summary>
    public UpdateSyncRuleMappingGenerationRequest? Generation { get; set; }

    /// <summary>
    /// Converts the request into the settings change the application layer understands.
    /// </summary>
    public SyncRuleMappingSettingsUpdate ToSettingsUpdate()
    {
        return new SyncRuleMappingSettingsUpdate
        {
            Expression = Expression,
            MissingInputBehaviour = MissingInputBehaviour,
            NullIsValue = NullIsValue,
            InboundValueProcessing = InboundValueProcessing,
            CaseNormalisation = CaseNormalisation,
            InitialExportOnly = InitialExportOnly,
            Enabled = Enabled,
            KeepUnique = KeepUnique,
            Generation = Generation?.ToSettingsUpdate()
        };
    }
}

/// <summary>
/// One source an Attribute Flow reads: an attribute or an expression.
/// </summary>
public class SyncRuleMappingSourceDto
{
    /// <summary>
    /// The ID of the source.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The source's position among the mapping's sources.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The ID of the Metaverse attribute an export mapping reads; null otherwise.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of that Metaverse attribute; null otherwise.
    /// </summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// The ID of the Connected System attribute an import mapping reads; null otherwise.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The name of that Connected System attribute; null otherwise.
    /// </summary>
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// The expression the source evaluates, reading attributes as mv["Name"] and cs["Name"]; null for an attribute
    /// source.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// What an expression source does when an attribute it reads has no value: EvaluateAnyway runs it regardless,
    /// ContributeNoValue skips it and contributes nothing, FailMapping skips it and records an error, and
    /// FailObject leaves the whole object untouched.
    /// </summary>
    public MissingInputBehaviour MissingInputBehaviour { get; set; }

    public static SyncRuleMappingSourceDto FromEntity(SyncRuleMappingSource entity)
    {
        return new SyncRuleMappingSourceDto
        {
            Id = entity.Id,
            Order = entity.Order,
            MetaverseAttributeId = entity.MetaverseAttributeId,
            MetaverseAttributeName = entity.MetaverseAttribute?.Name,
            ConnectedSystemAttributeId = entity.ConnectedSystemAttributeId,
            ConnectedSystemAttributeName = entity.ConnectedSystemAttribute?.Name,
            Expression = entity.Expression,
            MissingInputBehaviour = entity.MissingInputBehaviour
        };
    }
}

/// <summary>
/// Request DTO for creating a new SyncRuleMapping.
/// </summary>
public class CreateSyncRuleMappingRequest
{
    /// <summary>
    /// For import rules: The target Metaverse Attribute ID.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// For export rules: The target Connected System Attribute ID.
    /// </summary>
    public int? TargetConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// For import rules only: inbound text value-processing transforms applied to the value as it flows to
    /// the target Metaverse attribute (a comma-separated set of flag names, e.g. "TreatWhitespaceAsNoValue,
    /// TrimWhitespace"). When omitted, defaults to TreatWhitespaceAsNoValue. Ignored for export rules.
    /// </summary>
    public InboundValueProcessing? InboundValueProcessing { get; set; }

    /// <summary>
    /// For import rules only: case normalisation applied to the inbound text value (None, Upper, Lower or
    /// Title). When omitted, defaults to None. Ignored for export rules.
    /// </summary>
    public InboundCaseNormalisation? CaseNormalisation { get; set; }

    /// <summary>
    /// For import rules only: when true, a connected, in-scope contribution of null/absent asserts "no value" and
    /// stops attribute priority resolution falling through to lower-priority contributions ("Null is a value", #91).
    /// When omitted, defaults to false. Ignored for export rules. Priority itself is not set at creation: a new
    /// import mapping lands at the safe-addition default (lowest priority) and is ordered later via the
    /// attribute-priority-order endpoint.
    /// </summary>
    public bool? NullIsValue { get; set; }

    /// <summary>
    /// For export rules only: when true, the mapping only flows during the initial provisioning (Create)
    /// export; afterwards the target attribute is unmanaged by JIM on that Connected System Object and
    /// Drift Correction does not re-assert it (#223). When omitted, defaults to false. Ignored for
    /// import rules.
    /// </summary>
    public bool? InitialExportOnly { get; set; }

    /// <summary>
    /// Whether the mapping is evaluated by synchronisation from the moment it is created (#1485). When omitted,
    /// defaults to true (enabled). Supply false to create the mapping disabled, so it can be ordered and
    /// reviewed before it starts flowing values; a disabled mapping is skipped in both directions until it is
    /// enabled. Applies to import and export rules alike.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// The "Keep unique with ..." switch (Generated Value Forms, #2023) for an Expression mapping whose value is built from
    /// a generated value. When omitted, defaults to true. Supply false so a target's refusal of the value is reported as
    /// an ordinary export error rather than corrected by correcting the generated value. Refused for an attribute mapping
    /// and for a generated mapping.
    /// </summary>
    public bool? KeepUnique { get; set; }

    /// <summary>
    /// The sources for this mapping (attribute mappings or expressions).
    /// </summary>
    [Required]
    public List<CreateSyncRuleMappingSourceRequest> Sources { get; set; } = new();

    /// <summary>
    /// Makes this a "Generated Value" mapping (Unique Value Generation, #242, Phase 3): the mapping's value is its base
    /// expression (read from <see cref="Sources"/>, optional for Sequence and Random tokens) plus a uniqueness
    /// token. Omit for an ordinary attribute or Expression mapping. Allowed on both import and export rules;
    /// direction gating follows the same rule as <see cref="InitialExportOnly"/>.
    /// </summary>
    public CreateSyncRuleMappingGenerationRequest? Generation { get; set; }
}

/// <summary>
/// Request DTO for creating a SyncRuleMappingSource.
/// </summary>
public class CreateSyncRuleMappingSourceRequest
{
    /// <summary>
    /// The order of this source in the mapping.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// For export rules: The source Metaverse Attribute ID.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// For import rules: The source Connected System Attribute ID.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// An expression to evaluate for this source.
    /// Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
    /// Example: "CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// For expression sources: what to do when an attribute the expression reads has no value on the object being
    /// synchronised. Omit for EvaluateAnyway, which runs the expression regardless and is what JIM has always
    /// done. ContributeNoValue skips the mapping and resolves by Attribute Priority without reporting anything;
    /// FailMapping skips it and records an ExpressionMissingInput error while the object's other attributes still
    /// flow; FailObject leaves the whole object untouched. Ignored for attribute sources.
    /// </summary>
    public MissingInputBehaviour? MissingInputBehaviour { get; set; }
}

/// <summary>
/// Request DTO for testing an expression with sample attribute data.
/// </summary>
public class TestExpressionRequest
{
    /// <summary>
    /// The expression to test.
    /// Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
    /// </summary>
    public string Expression { get; set; } = null!;

    /// <summary>
    /// Sample Metaverse attribute values to use during evaluation.
    /// Keys are attribute names, values are the attribute values.
    /// </summary>
    public Dictionary<string, object?>? MvAttributes { get; set; }

    /// <summary>
    /// Sample Connected System attribute values to use during evaluation.
    /// Keys are attribute names, values are the attribute values.
    /// </summary>
    /// <remarks>
    /// With an Attribute Flow (<c>SyncRuleId</c> and <c>MappingId</c>), each value is converted to its attribute's type
    /// as the object would hold it: text, a number, a Boolean, null for no value (distinct from empty text), or an array
    /// of these for a multi-valued attribute. The same applies to <c>MvAttributes</c>.
    /// </remarks>
    public Dictionary<string, object?>? CsAttributes { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose Attribute Flow to test the expression as, given with <c>MappingId</c>. The test then
    /// runs what synchronisation runs: each sample converted to its attribute's type, the Attribute Flow's Missing Input
    /// Behaviour and value processing, and conversion to its target attribute's type. Omit both to evaluate the
    /// expression alone.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The Attribute Flow on the Synchronisation Rule to test the expression as, given with <c>SyncRuleId</c>. The
    /// expression tested is <c>Expression</c>, so an edit can be tried before it is saved.
    /// </summary>
    public int? MappingId { get; set; }

    /// <summary>
    /// A Connected System Object of the Synchronisation Rule's Connected System whose values to test with, for an import
    /// Attribute Flow. Its values for the attributes the expression reads are the inputs; a sample in
    /// <c>CsAttributes</c> replaces the object's value for that attribute. An expression that also reads <c>mv[...]</c>
    /// takes those inputs from the Metaverse Object the Connected System Object is joined to.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// A Metaverse Object whose values to test with, for an export Attribute Flow. A sample in <c>MvAttributes</c>
    /// replaces the object's value for that attribute.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The instant to test as of, in UTC: what <c>Now()</c> and <c>Today()</c> read. Omit it for the current time.
    /// </summary>
    public DateTime? AsOf { get; set; }
}

/// <summary>
/// One input an expression read when it was tested as an Attribute Flow.
/// </summary>
public class TestExpressionInputDto
{
    /// <summary>
    /// The input as the expression writes it, such as <c>cs["hireDate"]</c>.
    /// </summary>
    public string Input { get; set; } = null!;

    /// <summary>
    /// The attribute's type, or null when the object being synchronised cannot hold a value for it.
    /// </summary>
    public AttributeDataType? Type { get; set; }

    /// <summary>
    /// Whether the attribute holds one value or several; null when <c>Type</c> is null.
    /// </summary>
    public AttributePlurality? Plurality { get; set; }

    /// <summary>
    /// The values the input was given, one line per value in JIM's text form. Empty when it had no value.
    /// </summary>
    public IReadOnlyList<string> ValueLines { get; set; } = [];
}

/// <summary>
/// What an Attribute Flow would do if one of its inputs had no value.
/// </summary>
public class TestExpressionAbsentInputDto
{
    /// <summary>
    /// The input made absent, such as <c>cs["sn"]</c>.
    /// </summary>
    public string Input { get; set; } = null!;

    /// <summary>
    /// What the target attribute would then be given. Null for an Attribute Flow that generates its value; see
    /// <c>BaseOutcome</c>.
    /// </summary>
    public AttributeFlowOutcome? Outcome { get; set; }

    /// <summary>
    /// For an Attribute Flow that generates its value, what its base expression would then give generation: a base,
    /// a wait, or an error. Null for any other Attribute Flow.
    /// </summary>
    public GeneratedBaseOutcome? BaseOutcome { get; set; }

    /// <summary>
    /// Why that outcome is an error; null when it is not.
    /// </summary>
    public AttributeFlowErrorReason? ErrorReason { get; set; }

    /// <summary>
    /// What synchronisation would then do, in one sentence.
    /// </summary>
    public string OutcomeDescription { get; set; } = null!;
}

/// <summary>
/// Response DTO for expression test results.
/// </summary>
public class TestExpressionResponse
{
    /// <summary>
    /// Indicates whether the expression is valid and evaluated successfully.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// The result of evaluating the expression (if successful).
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// The result as the portal's expression tester shows it: one line per value, each written as JIM writes the value
    /// as text (a date as 2026-03-15 10:30:00Z, a Binary value described by its size). Empty when the expression
    /// produced no value. Not set when it failed; <c>ErrorMessage</c> says why.
    /// </summary>
    public IReadOnlyList<string>? ResultLines { get; set; }

    /// <summary>
    /// The type of the result (e.g., "String", "Int32", "Boolean").
    /// </summary>
    public string? ResultType { get; set; }

    /// <summary>
    /// Error message if the expression is invalid or evaluation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Position in the expression where an error occurred (if applicable).
    /// </summary>
    public int? ErrorPosition { get; set; }

    /// <summary>
    /// What the Attribute Flow's target attribute would be given: values, no value, or an error. Set only when an
    /// Attribute Flow was given and its sample values could be used.
    /// </summary>
    public AttributeFlowOutcome? Outcome { get; set; }

    /// <summary>
    /// Why the outcome is an error. Set only when <c>Outcome</c> is Error.
    /// </summary>
    public AttributeFlowErrorReason? ErrorReason { get; set; }

    /// <summary>
    /// What synchronisation would do, in one sentence. Set only when an Attribute Flow was given.
    /// </summary>
    public string? OutcomeDescription { get; set; }

    /// <summary>
    /// The values the target attribute would hold after value processing and conversion to its type, one line per value
    /// in JIM's text form. Set only when <c>Outcome</c> is Values.
    /// </summary>
    public IReadOnlyList<string>? ValueLines { get; set; }

    /// <summary>
    /// The Attribute Flow's target attribute. Set only when an Attribute Flow was given.
    /// </summary>
    public string? TargetAttributeName { get; set; }

    /// <summary>
    /// The target attribute's type, which the values are converted to. Set only when an Attribute Flow was given.
    /// </summary>
    public AttributeDataType? TargetAttributeType { get; set; }

    /// <summary>
    /// The inputs the expression reads that have no value, such as <c>cs["sn"]</c>, when the Attribute Flow's Missing
    /// Input Behaviour stopped it being evaluated.
    /// </summary>
    public IReadOnlyList<string>? MissingInputs { get; set; }

    /// <summary>
    /// Why the sample values could not be used, one message each, such as text that is not a date for a date and time
    /// attribute. When present the expression was not evaluated.
    /// </summary>
    public IReadOnlyList<string>? InputErrors { get; set; }

    /// <summary>
    /// The inputs the expression read, with the values they were given: the samples, and any loaded from an object.
    /// Set only when an Attribute Flow was given.
    /// </summary>
    public IReadOnlyList<TestExpressionInputDto>? Inputs { get; set; }

    /// <summary>
    /// The value processing the Attribute Flow applies to the expression's result, in the order it applies them. Set
    /// only for an import Attribute Flow to a Text attribute whose expression returned a result; empty when none is set.
    /// </summary>
    public IReadOnlyList<AttributeFlowValueProcessingStep>? ValueProcessingSteps { get; set; }

    /// <summary>
    /// Whether value processing changed the expression's result. Set with <c>ValueProcessingSteps</c>.
    /// </summary>
    public bool? ValueProcessingChanged { get; set; }

    /// <summary>
    /// For each input given a value, what the Attribute Flow would do with that input absent. Set only when an Attribute
    /// Flow was given and its sample values could be used.
    /// </summary>
    public IReadOnlyList<TestExpressionAbsentInputDto>? IfInputHasNoValue { get; set; }

    /// <summary>
    /// True when the Attribute Flow generates its value (a Generated Value). The test then stops before generation, as
    /// synchronisation does: <c>BaseOutcome</c> and <c>BaseValue</c> say what generation would be given, and
    /// <c>Outcome</c> is not set. Set only when an Attribute Flow was given.
    /// </summary>
    public bool? GeneratedValue { get; set; }

    /// <summary>
    /// For a Generated Value, what its base expression gives generation: a base, a wait (no value yet), no base
    /// expression (the token alone is generated), or an error.
    /// </summary>
    public GeneratedBaseOutcome? BaseOutcome { get; set; }

    /// <summary>
    /// For a Generated Value, the base the uniqueness token would be added to, after value processing. Set only when
    /// <c>BaseOutcome</c> is Base. Which candidate is free is decided when synchronisation runs.
    /// </summary>
    public string? BaseValue { get; set; }
}
